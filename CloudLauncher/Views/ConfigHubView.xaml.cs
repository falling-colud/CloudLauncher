using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using Kind = CloudLauncher.Services.ConfigHubService.FileKind;
using Entry = CloudLauncher.Services.ConfigHubService.Entry;
using GroupState = CloudLauncher.Services.ConfigHubService.GroupState;
using FileEdit = CloudLauncher.Services.ConfigEditTracker.FileEdit;
using DefaultCopyState = CloudLauncher.Services.ConfigEditTracker.DefaultCopyState;

namespace CloudLauncher.Views;

/// <summary>
/// The Config and scripts page: read, edit, compare and copy the config files and KubeJS scripts
/// inside each instance from one place.
/// </summary>
/// <remarks>
/// <para>Each row is a game-relative path (<c>config/jei/jei-client.ini</c>) with the instances that
/// have a file there and whether their copies match. All disk work runs in <see cref="Task.Run"/>
/// behind <see cref="_workCts"/>; <see cref="ConfigHubService"/> remembers scans across launches so
/// the first frame shows the last results. Counts and empty panels go through
/// <see cref="PageState"/>, and nothing is shown before a scan has finished.</para>
/// <para>The same page is the Configs tab of one instance's Modpack Management, pinned to that
/// instance (see <see cref="_scope"/>). It still scans every instance, so compare and "copy to other
/// instances" work as they do here, and the two share one scan cache.</para>
/// <para>Every scan also asks <see cref="ConfigEditTracker"/> which files have been edited since the
/// launcher first saw them, which drives the Edited chip and the defaultconfigs actions.</para>
/// </remarks>
public partial class ConfigHubView : Page, IReusablePage, IRefreshablePage
{
    private readonly MainWindow _shell;

    /// <summary>The one instance this page lists when it is an instance's Configs tab, or null for the
    /// cross-instance page. Replaces the instance picker.</summary>
    private readonly PackSummary? _scope;

    /// <summary>Which files are edited and how they stand against defaultconfigs. Replaced by every
    /// scan, like <see cref="_scan"/>.</summary>
    private ConfigEditTracker.Snapshot _edits = ConfigEditTracker.Snapshot.Empty;

    /// <summary>The Edited chip: list only files that differ from their baseline.</summary>
    private bool _editedOnly;

    private readonly ObservableCollection<FileRow> _rows = new();
    private readonly ObservableCollection<KindChip> _kindChips = new();
    private readonly ObservableCollection<CopyRow> _copies = new();
    private readonly ObservableCollection<HitRow> _hits = new();

    /// <summary>Owns every count, empty panel, error and busy affordance on this page.</summary>
    private readonly PageState _state;

    /// <summary>Held while the instance ComboBox is being filled, so the SelectionChanged it raises
    /// synchronously is not mistaken for the user picking an instance.</summary>
    private readonly Reentrancy _filling = new();

    /// <summary>True once a scan, remembered or fresh, has completed. Until then
    /// <see cref="Refresh"/> leaves the screen to <see cref="_state"/>.</summary>
    private bool _scanned;

    /// <summary>The standing note under the list: how old the remembered scan is, or why the server
    /// isn't answering. Re-applied on every rebuild so a filter change doesn't drop it.</summary>
    private string? _note;

    private List<PackSummary> _packs = new();
    private List<Entry> _scan = new();
    private Dictionary<string, GroupState> _groupStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>KubeJS error lines per instance, keyed by script file name. Filled by the scan.</summary>
    private Dictionary<Guid, Dictionary<string, List<string>>> _kubeErrors = new();

    /// <summary>Content-search hits keyed by full path, or null when no content search is active.
    /// Non-null with zero entries for a search that matched nothing, which is a different state.</summary>
    private Dictionary<string, List<ConfigHubService.SearchHit>>? _contentHits;

    private Kind? _kindFilter;
    private SortMode _sort = SortMode.Path;
    private bool _differsOnly;
    private bool _loading;

    /// <summary>Cancels the page's background work. Only one job runs at a time, since a scan and a
    /// copy racing over the same tree could lose a config.</summary>
    private CancellationTokenSource? _workCts;

    /// <summary>Guards the async detail load against a stale result arriving after the user has moved
    /// on to another row.</summary>
    private int _detailToken;

    private enum SortMode { Name, Path, Modified, Size, Diff }

    public ConfigHubView(MainWindow shell) : this(shell, scope: null) { }

    /// <summary>The page pinned to one instance, for the Configs tab of its Modpack
    /// Management.</summary>
    public ConfigHubView(MainWindow shell, PackDetail pack) : this(shell, ScopeOf(pack)) { }

    private ConfigHubView(MainWindow shell, PackSummary? scope)
    {
        InitializeComponent();
        _shell = shell;
        _scope = scope;
        FileList.ItemsSource = _rows;
        KindStrip.ItemsSource = _kindChips;
        CopiesList.ItemsSource = _copies;
        HitsList.ItemsSource = _hits;

        _state = new PageState(FileList, PageStateHost, nameof(ConfigHubView))
            .Copy(scope is null ? PageCopy.ConfigFiles : ScopedCopy)
            .Slots(CountLabel, StatusLabel, BusyBar, BusyCancelButton)
            .DisableWhileBusy(RefreshButton, SortButton, DiffersToggle, PackFilterBox, DefaultsButton);
        _state.RetryRequested += () => _ = LoadAsync(force: true);
        _state.CancelRequested += () => _workCts?.Cancel();

        // Filters an in-memory list, so a short debounce is fine.
        SearchBox.DebounceMilliseconds = 200;
        SearchBox.TextChanged += (_, _) => Refresh();

        // The instance tab starts on the plain list: the hub's remembered filter belongs to the hub.
        if (scope is null) RestoreFilter();
        else ApplyScopedLayout(scope);
        DiffersToggle.IsChecked = _differsOnly;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _workCts?.Cancel();
    }

    /// <summary>The page's wording for one instance rather than all of them.</summary>
    private static readonly PageCopy ScopedCopy = PageCopy.ConfigFiles with
    {
        LoadingLine = "Reading this instance's config, kubejs and defaultconfigs folders.",
        EmptyBody = "This instance has no config, kubejs or defaultconfigs folder yet. "
                  + "That is normal until it has been launched once."
    };

    private static PackSummary ScopeOf(PackDetail pack) => new(
        pack.Id, pack.Name, pack.Description, pack.OwnerId, pack.OwnerUsername, pack.Visibility,
        pack.IsShared, pack.IsEmpty, pack.MinecraftVersion, pack.Loader, pack.LoaderVersion,
        pack.CreatedAt, pack.UpdatedAt, pack.EffectivePermissions, pack.Summary);

    /// <summary>
    /// Fits the page into a tab: no page title (the tab says it), no instance picker, and the
    /// side margins dropped, since the tab's own page already has them.
    /// </summary>
    /// <remarks>The defaultconfigs button is hidden where the loader never reads that folder; the
    /// detail pane says why.</remarks>
    private void ApplyScopedLayout(PackSummary scope)
    {
        HeaderPanel.Visibility = Visibility.Collapsed;
        Divider.Visibility = Visibility.Collapsed;
        PackFilterBox.Visibility = Visibility.Collapsed;
        FilterBar.Margin = new Thickness(0, 0, 0, 6);
        ChipRow.Margin = new Thickness(0, 4, 0, 12);
        ContentSearchBar.Margin = new Thickness(0, 0, 0, 12);
        ListArea.Margin = new Thickness(0, 0, 0, 12);
        SearchInFilesButton.ToolTip = "Search inside every config and script in this instance";
        if (ConfigHubService.NoDefaultConfigsReason(scope.Loader) is not null)
            DefaultsButton.Visibility = Visibility.Collapsed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await LoadAsync(force: false);

    // ── loading ──────────────────────────────────────────────────────────────

    /// <summary>Re-runs the load in place, keeping the rows and the scroll position (offline banner's
    /// Retry, page reopen).</summary>
    public Task RefreshAsync() => LoadAsync(force: true);

    private async Task LoadAsync(bool force, string? note = null)
    {
        if (_loading) return;
        _loading = true;
        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        var ct = _workCts.Token;

        try
        {
            // Shows the remembered scan first (first load only), then re-reads every instance. Without
            // force (a reopen) this is quiet: nothing moves unless the walk runs long or finds a change.
            _state.Begin(null, null, quiet: !force);

            // ListPacksAsync falls back to its cache or the folders on this PC when the server is
            // unreachable, so this works offline.
            var listed = (await (force ? App.State.Api.ListPacksAsync(ct) : App.State.Api.ListPacksQuickAsync(ct)))
                .Where(p => !App.State.Settings.IsPackHidden(p.Id) || p.Id == _scope?.Id)
                .ToList();
            // The instance a tab was opened for is listed even when the list does not have it yet.
            if (_scope is { } pinned && listed.All(p => p.Id != pinned.Id)) listed.Add(pinned);
            _packs = listed.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            using (_filling.Hold()) RebuildPackFilter();

            var packs = _packs;
            var settings = App.State.Settings;
            var folders = App.State.Packs;
            // A tab shows one instance's edits, so only that one is hashed.
            var tracked = _scope is { } only ? packs.Where(p => p.Id == only.Id).ToList() : packs;

            // First load only; later loads already have rows on screen.
            if (!force && !_scanned)
            {
                await ConfigHubService.WarmAsync();
                // Throw rather than return, or the busy bar keeps running after Cancel.
                ct.ThrowIfCancellationRequested();
                if (ConfigHubService.Cached(packs, folders) is { } remembered)
                {
                    // Stored answers only: nothing is hashed or written before the first frame.
                    var rememberedEdits = await Task.Run(() => ConfigEditTracker.Evaluate(
                        tracked, remembered.Files, folders, rehash: false, null, ct), ct);
                    Apply(remembered, rememberedEdits);
                    var rememberedNote = remembered.ScannedUtc is { } when && PackListCache.Describe(when) is { } age
                        ? $"Scanned {age}. Re-reading every instance now."
                        : null;
                    // The "scanned X ago" note only shows with the refresh bar, if the walk is still running.
                    _note = null;
                    Refresh();
                    // Keep the remembered rows on screen while re-reading.
                    _state.Begin(null, refreshing: true, quiet: true, onReveal: () =>
                    {
                        _note = rememberedNote;
                        _state.Note(rememberedNote);
                    });
                }
            }

            var progress = new Progress<string>(name => _state.Progress($"Scanning {name}..."));
            var editProgress = new Progress<string>(name => _state.Progress($"Checking {name} for edited files..."));
            var (result, edits) = await Task.Run(() =>
            {
                var scan = ConfigHubService.Rescan(packs, folders, settings, force, progress, ct);
                return (scan, ConfigEditTracker.Evaluate(tracked, scan.Files, folders, rehash: true, editProgress, ct));
            }, ct);

            Apply(result, edits);
            var stale = App.State.Api.PackListStale is { Length: > 0 } why
                ? $"Showing your last known instances - the server is not answering ({why}). " +
                  "The files on this PC are still readable and editable."
                : null;
            _note = string.Join("  ·  ",
                new[] { note, stale }.Where(p => !string.IsNullOrEmpty(p)));
            if (_note.Length == 0) _note = null;
            Refresh();
        }
        catch (OperationCanceledException) { _state.Cancelled("Scan cancelled."); }
        catch (OfflineException ex)
        {
            _state.Offline(ex.Reason ?? "the server is not answering");
        }
        catch (Exception ex)
        {
            _state.Error("The config, kubejs and defaultconfigs folders could not be read.", ex);
        }
        finally { _loading = false; }
    }

    private void Apply(ConfigHubService.ScanResult result, ConfigEditTracker.Snapshot edits)
    {
        _scan = result.Files;
        _edits = edits;
        _groupStates = result.Groups;
        _kubeErrors = result.KubeErrors;
        _contentHits = null;
        _scanned = true;
    }

    /// <summary>Fills the instance ComboBox. Callers hold <see cref="_filling"/>, since assigning
    /// ItemsSource and SelectedItem raises SelectionChanged synchronously.</summary>
    private void RebuildPackFilter()
    {
        var items = new List<PackFilterItem> { new(null, "All instances") };
        items.AddRange(_packs.Select(p => new PackFilterItem(p.Id, p.Name)));
        var previous = SelectedPackId ?? _restoredPackId;
        _restoredPackId = null;
        PackFilterBox.ItemsSource = items;
        PackFilterBox.SelectedItem = items.FirstOrDefault(i => i.Id == previous) ?? items[0];
    }

    /// <summary>The instance whose files are listed, or null for all of them. Always the pinned one in
    /// an instance's tab.</summary>
    private Guid? SelectedPackId => _scope?.Id ?? (PackFilterBox.SelectedItem as PackFilterItem)?.Id;

    /// <summary>The instance a new file is created in, or whose folder the toolbar opens: the filtered
    /// one, or the first instance when the filter is on "All".</summary>
    private Guid? TargetPackId => SelectedPackId ?? _packs.FirstOrDefault()?.Id;

    private string PackName(Guid id) => _packs.FirstOrDefault(p => p.Id == id)?.Name ?? "an instance";

    // ── building the list ────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the rows from the scan in memory. No disk access, so it is safe to call on every
    /// filter, sort and search change.
    /// </summary>
    /// <remarks>Does nothing until a scan has completed, so no count or empty state is shown for a list
    /// that hasn't been filled yet.</remarks>
    private void Refresh()
    {
        if (!_scanned) return;

        var previous = SelectedRow is { } sel ? (sel.PackId, sel.RelativePath) : ((Guid, string)?)null;

        var scope = SelectedPackId is { } packId
            ? _scan.Where(e => e.PackId == packId).ToList()
            : _scan;

        // Group across the whole scan even with one instance selected, so "in 5 instances, 2 differ"
        // still shows.
        var byPath = _scan.GroupBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
                          .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var query = SearchBox.Text?.Trim() ?? "";
        var pins = App.State.Settings.ConfigHubPins;

        // With "All instances", one row per relative path: the first instance alphabetically is the
        // row's copy and the rest appear in the detail pane. A content search lists every hit, and so
        // does the Edited chip: an edit belongs to one instance's copy, which may not be the first.
        var candidates = scope;
        if (SelectedPackId is null && _contentHits is null && !_editedOnly)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            candidates = scope.Where(e => seen.Add(e.RelativePath)).ToList();
        }
        if (_editedOnly) candidates = candidates.Where(IsEdited).ToList();

        // Chip counts are taken after de-duplication and the Edited chip, and before the kind and
        // search filters, so they match the rows each chip would show.
        var counts = candidates.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
        var editedCount = scope.Count(IsEdited);

        var rows = new List<FileRow>();
        foreach (var entry in candidates)
        {
            if (_kindFilter is { } kind && entry.Kind != kind) continue;
            if (query.Length > 0 && !MatchesQuery(entry, query)) continue;

            var copies = byPath.GetValueOrDefault(entry.RelativePath) ?? [entry];
            var state = copies.Count < 2
                ? GroupState.Unique
                : _groupStates.GetValueOrDefault(entry.RelativePath, GroupState.Unknown);
            if (_differsOnly && state != GroupState.Differs) continue;

            var hits = _contentHits?.GetValueOrDefault(entry.FullPath);
            if (_contentHits is not null && (hits is null || hits.Count == 0)) continue;

            rows.Add(new FileRow(entry, copies, state,
                pinned: pins.Contains(ConfigHubService.PinKey(entry.PackId, entry.RelativePath)),
                errorCount: ErrorsFor(entry).Count,
                hitCount: hits?.Count ?? 0,
                edit: _edits.For(entry)));
        }

        // A pin whose file is gone is shown greyed rather than dropped; the instance may be mid-sync.
        if (!_differsOnly && !_editedOnly && _contentHits is null) rows.AddRange(MissingPinRows(rows, query));

        Sort(rows);
        _rows.Clear();
        foreach (var row in rows) _rows.Add(row);

        RebuildKindChips(counts, candidates.Count);
        RebuildEditedChip(editedCount);
        if (_rows.Count == 0) ChooseEmptyCopy(query);
        _state.Content(_rows.Count, countText: ScopeLabel(candidates.Count, editedCount),
            note: _editedOnly ? JoinNotes(_note, TrackingNote()) : _note);

        if (previous is { } key)
        {
            var restored = _rows.FirstOrDefault(r => r.PackId == key.Item1 && r.RelativePath == key.Item2);
            if (restored is not null) FileList.SelectedItem = restored;
            else ShowDetail(null);
        }
    }

    private bool IsEdited(Entry entry) => _edits.For(entry)?.Edited == true;

    private static string? JoinNotes(params string?[] notes)
    {
        var joined = string.Join("  ·  ", notes.Where(n => !string.IsNullOrEmpty(n)));
        return joined.Length == 0 ? null : joined;
    }

    /// <summary>
    /// The honest limit of edit tracking, for the status line while the Edited chip is on: tracking
    /// starts the first time the launcher scans an instance, and nothing earlier can be known.
    /// </summary>
    private string? TrackingNote()
    {
        if (SelectedPackId is { } id)
            return _edits.TrackingSince.TryGetValue(id, out var since)
                ? $"Edits are tracked from {TimeFormat.DateTime(since)}. Anything changed before then " +
                  "isn't known - compare with defaultconfigs where there is a copy."
                : null;
        if (_edits.TrackingSince.Count == 0) return null;
        var latest = _edits.TrackingSince.Values.Max();
        return "Edits are tracked from the first time each instance was scanned " +
               $"(the latest on {TimeFormat.DateTime(latest)}). Anything changed before then isn't known.";
    }

    /// <summary>The Edited chip. Drawn with the kind chips' look but on its own, since it combines
    /// with any kind.</summary>
    private void RebuildEditedChip(int count)
    {
        var since = SelectedPackId is { } id && _edits.TrackingSince.TryGetValue(id, out var when)
            ? $" Tracked since {TimeFormat.DateTime(when)}; earlier edits aren't known."
            : "";
        EditedChip.DataContext = new KindChip(null, "Edited", count, _editedOnly,
            "Only files changed since the launcher first saw them: in the game, in the editor or by hand. " +
            "A mod update that adds settings changes its config too." + since);
    }

    private static bool MatchesQuery(Entry entry, string query) =>
        entry.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)
        || entry.PackName.Contains(query, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<FileRow> MissingPinRows(List<FileRow> present, string query)
    {
        var have = present.Select(r => ConfigHubService.PinKey(r.PackId, r.RelativePath))
                          .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pin in App.State.Settings.ConfigHubPins)
        {
            if (have.Contains(pin)) continue;
            var slash = pin.IndexOf('/');
            if (slash <= 0 || !Guid.TryParseExact(pin[..slash], "N", out var packId)) continue;
            var rel = pin[(slash + 1)..];
            if (SelectedPackId is { } only && only != packId) continue;
            if (_kindFilter is not null) continue;
            if (query.Length > 0 && !rel.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            // The file is gone from the scan, so there is nothing to size or date.
            var ghost = new Entry(packId, PackName(packId), rel, "", 0, DateTime.MinValue, Kind.Config);
            yield return new FileRow(ghost, [], GroupState.Unique, pinned: true, errorCount: 0,
                hitCount: 0, missing: true);
        }
    }

    private void Sort(List<FileRow> rows)
    {
        // Pins always go to the top, whatever the sort.
        Comparison<FileRow> comparison = _sort switch
        {
            SortMode.Name => (a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase),
            SortMode.Modified => (a, b) => b.Entry.ModifiedUtc.CompareTo(a.Entry.ModifiedUtc),
            SortMode.Size => (a, b) => b.Entry.Size.CompareTo(a.Entry.Size),
            SortMode.Diff => (a, b) => DiffRank(a).CompareTo(DiffRank(b)),
            _ => (a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase)
        };
        rows.Sort((a, b) =>
        {
            if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
            var primary = comparison(a, b);
            return primary != 0
                ? primary
                : string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static int DiffRank(FileRow row) => row.State switch
    {
        GroupState.Differs => 0,
        GroupState.Unknown => 1,
        GroupState.Identical => 2,
        _ => 3
    };

    /// <summary>The kind strip. Null counts mean not counted yet and show as a dash, not 0.</summary>
    private void RebuildKindChips(Dictionary<Kind, int>? counts, int? total)
    {
        int? For(Kind kind) => counts?.GetValueOrDefault(kind);
        var chips = new List<KindChip>
        {
            new(null, "All", total, _kindFilter is null, "Every config file, script and default config"),
            new(Kind.Config, "Config", For(Kind.Config), _kindFilter == Kind.Config,
                "game/config - the mod settings for this instance"),
            new(Kind.KubeJs, "KubeJS", For(Kind.KubeJs), _kindFilter == Kind.KubeJs,
                "game/kubejs - startup, server and client scripts, plus the data and assets they generate"),
            new(Kind.DefaultConfigs, "Defaults", For(Kind.DefaultConfigs),
                _kindFilter == Kind.DefaultConfigs,
                "game/defaultconfigs - what the pack copies into each NEW world, not what the game is using now"),
            new(Kind.ServerConfig, "World server", For(Kind.ServerConfig),
                _kindFilter == Kind.ServerConfig,
                "game/saves/<world>/serverconfig - one world's server settings"),
            new(Kind.KubeJsLog, "KubeJS logs", For(Kind.KubeJsLog),
                _kindFilter == Kind.KubeJsLog,
                "game/logs/kubejs - where a script error is reported")
        };
        _kindChips.Clear();
        foreach (var chip in chips) _kindChips.Add(chip);
    }

    /// <summary>The count slot text for the current filter. Only called with counts from a
    /// completed scan.</summary>
    private string ScopeLabel(int scopeCount, int editedCount)
    {
        if (_packs.Count == 0) return "";
        if (_scope is { } pinned)
        {
            // Only this instance's paths: a difference between two other instances is not news here.
            var mine = _scan.Where(e => e.PackId == pinned.Id).Select(e => e.RelativePath)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var differs = mine.Count(rel => _groupStates.GetValueOrDefault(rel) == GroupState.Differs);
            return $"{scopeCount:N0} file(s) in this instance" +
                   (editedCount > 0 ? $" · {editedCount:N0} edited" : "") +
                   (differs > 0 ? $" · {differs:N0} differ from another instance" : "");
        }
        var differing = _groupStates.Count(kv => kv.Value == GroupState.Differs);
        return (SelectedPackId is { } id
                   ? $"{scopeCount:N0} file(s) in {PackName(id)}"
                   : $"{scopeCount:N0} distinct path(s) across {_packs.Count} instances") +
               (differing > 0
                   ? $" · {differing:N0} path(s) differ between instances"
                   : _groupStates.Count > 0 ? " · every shared path matches" : "");
    }

    /// <summary>
    /// Picks the empty-state wording for this pass.
    /// </summary>
    /// <remarks>Chosen per pass so "nothing matched that filter" doesn't linger after the filter is
    /// cleared.</remarks>
    private void ChooseEmptyCopy(string query)
    {
        if (_packs.Count == 0)
            _state.EmptyNext("No instances yet",
                "Create or download an instance and its config files and KubeJS scripts appear here.",
                PageCopy.ConfigFiles.Glyph);
        else if (_contentHits is not null)
            _state.EmptyNext("Nothing matched",
                $"No config or script contains '{ContentQueryBox.Text.Trim()}'. " +
                "Very large files are skipped - the status line says how many.");
        else if (_differsOnly)
            _state.EmptyNext("Everything matches",
                "No file at the same path differs between your instances. " +
                "Turn the differences filter off to see them all.");
        else if (_editedOnly && _kindFilter is null && query.Length == 0)
            _state.EmptyNext("Nothing edited",
                "Every file is still as the launcher first saw it. Edits made in the game, in the editor " +
                "or by hand show up here after the next scan.");
        else if (query.Length > 0 || _kindFilter is not null)
            _state.EmptyFiltered();
        // Otherwise the default wording from PageCopy.ConfigFiles applies: no instance has these
        // folders yet.
    }

    private List<string> ErrorsFor(Entry entry)
    {
        if (entry.Kind != Kind.KubeJs) return [];
        if (!_kubeErrors.TryGetValue(entry.PackId, out var byScript)) return [];
        return byScript.GetValueOrDefault(entry.FileName) ?? [];
    }

    // ── the detail pane ──────────────────────────────────────────────────────

    private FileRow? SelectedRow => FileList.SelectedItems.Count == 1
        ? FileList.SelectedItem as FileRow
        : null;

    private void ShowDetail(FileRow? row)
    {
        var token = ++_detailToken;
        _copies.Clear();
        _hits.Clear();

        if (row is null || row.Missing)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            DetailPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        DetailPanel.Visibility = Visibility.Visible;
        DetailPlaceholder.Visibility = Visibility.Collapsed;

        DetailName.Text = row.FileName;
        DetailPath.Text = row.Entry.FullPath;
        DetailMeta.Text = $"{ConfigHubService.KindLabel(row.Entry.Kind)} · " +
                          $"{ConfigHubService.FormatSize(row.Entry.Size)} · " +
                          $"modified {ConfigHubService.FormatAge(row.Entry.ModifiedUtc)} · in {row.Entry.PackName}";
        DetailCompareButton.IsEnabled = row.Copies.Count > 1;

        var errors = ErrorsFor(row.Entry);
        ErrorPanel.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = string.Join("\n", errors);

        ShowEditDetail(row);

        CopiesHeader.Text = row.Copies.Count > 1
            ? $"ACROSS INSTANCES - {row.Copies.Count}"
            : "ACROSS INSTANCES - ONLY THIS ONE";

        if (_contentHits?.GetValueOrDefault(row.Entry.FullPath) is { Count: > 0 } hits)
        {
            HitsPanel.Visibility = Visibility.Visible;
            HitsHeader.Text = $"MATCHES - {hits.Count}";
            foreach (var hit in hits) _hits.Add(new HitRow(hit));
        }
        else
        {
            HitsPanel.Visibility = Visibility.Collapsed;
        }

        _ = LoadDetailAsync(row, token);
    }

    /// <summary>
    /// The detail pane's edit box: whether the file is edited, how it stands against its
    /// defaultconfigs copy, and the buttons that go with that.
    /// </summary>
    /// <remarks>Says plainly what can't be known (edits before the file was first seen) and why an
    /// action is missing (not a file FML reads from defaultconfigs, or a loader without it), rather
    /// than leaving a button out without a word.</remarks>
    private void ShowEditDetail(FileRow row)
    {
        var entry = row.Entry;
        if (!ConfigEditTracker.IsTracked(entry.Kind))
        {
            EditPanel.Visibility = Visibility.Collapsed;
            return;
        }
        EditPanel.Visibility = Visibility.Visible;

        var edit = row.Edit;
        EditHeader.Text = edit switch
        {
            null => "Not checked for edits yet",
            { Created: true } => "Edited - created in the launcher",
            { Edited: true } => "Edited since the launcher first saw it",
            _ => "Not edited"
        };
        EditText.Text = edit switch
        {
            null => "The next scan records this file as it is now and checks it from then on.",
            { Created: true } => "No mod wrote this file, so it counts as edited until you mark it otherwise.",
            _ => $"Compared with the copy first seen {TimeFormat.DateTime(edit!.FirstSeenUtc ?? DateTimeOffset.UtcNow)}. " +
                 "Edits made before then aren't known."
        };

        var loader = _packs.FirstOrDefault(p => p.Id == entry.PackId)?.Loader;
        var noLoader = loader is { } l ? ConfigHubService.NoDefaultConfigsReason(l) : null;
        var counterpart = DefaultsCounterpart(entry);
        string defaults;
        if (entry.Kind == Kind.DefaultConfigs)
        {
            defaults = counterpart is null
                ? "FML copies this into config when the file there is missing. This instance has no copy in use."
                : $"FML copies this into place when {counterpart.RelativePath} is missing.";
        }
        else if (noLoader is not null) defaults = noLoader;
        else if (ConfigHubService.DefaultConfigsPath(entry) is not { } target)
            defaults = entry.Kind is Kind.Config or Kind.ServerConfig
                ? $"FML never reads this from defaultconfigs: {ConfigHubService.WhyNotDefaultConfigs(entry)}."
                : "";
        else defaults = row.Edit?.DefaultState switch
        {
            DefaultCopyState.Same => $"{target} is identical.",
            DefaultCopyState.Differs => $"{target} holds a different version.",
            DefaultCopyState.Unknown => $"{target} exists but could not be read to compare.",
            _ => $"Not in defaultconfigs yet ({target})."
        };
        DefaultsText.Text = defaults;
        DefaultsText.Visibility = defaults.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        EditCompareDefaultsButton.Visibility = counterpart is not null ? Visibility.Visible : Visibility.Collapsed;
        EditToDefaultsButton.Visibility =
            noLoader is null && ConfigHubService.DefaultConfigsPath(entry) is not null
                             && row.Edit?.DefaultState != DefaultCopyState.Same
                ? Visibility.Visible
                : Visibility.Collapsed;
        EditToDefaultsButton.Content = row.Edit?.DefaultState is DefaultCopyState.Differs or DefaultCopyState.Unknown
            ? "Update defaultconfigs"
            : "Copy to defaultconfigs";
        EditResetButton.Visibility = row.IsEdited ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The other half of a config / defaultconfigs pair in the same instance: a config's default, or
    /// the config a default fills in. Null when there is none in the scan.
    /// </summary>
    /// <remarks>A default can fill in both <c>config/</c> and a world's <c>serverconfig/</c>; the
    /// <c>config/</c> copy is preferred, since that is the one every world falls back to.</remarks>
    private Entry? DefaultsCounterpart(Entry entry)
    {
        if (entry.Kind == Kind.DefaultConfigs)
        {
            return _scan.Where(e => e.PackId == entry.PackId && e.Kind is Kind.Config or Kind.ServerConfig
                                    && string.Equals(ConfigHubService.DefaultConfigsPath(e), entry.RelativePath,
                                                     StringComparison.OrdinalIgnoreCase))
                        .OrderBy(e => e.Kind == Kind.Config ? 0 : 1)
                        .ThenBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
        }
        if (ConfigHubService.DefaultConfigsPath(entry) is not { } target) return null;
        return _scan.FirstOrDefault(e => e.PackId == entry.PackId
                                         && string.Equals(e.RelativePath, target, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fills in the per-instance comparison and the preview. Both read the disk, so both happen off the
    /// UI thread; <paramref name="token"/> drops the result if the user has since picked another row.
    /// </summary>
    private async Task LoadDetailAsync(FileRow row, int token)
    {
        var copies = row.Copies;
        var selfPath = row.Entry.FullPath;
        try
        {
            var (rows, preview, note) = await Task.Run(() =>
            {
                var selfHash = ConfigHubService.HashOrNull(selfPath);
                var list = copies
                    .OrderBy(c => c.PackName, StringComparer.OrdinalIgnoreCase)
                    .Select(c => new CopyRow(c,
                        isSelf: c.FullPath == selfPath,
                        same: c.FullPath == selfPath
                            ? true
                            : selfHash is not null && c.Size <= ConfigHubService.MaxInspectBytes
                                ? ConfigHubService.HashOrNull(c.FullPath) == selfHash
                                : null))
                    .ToList();

                return (list, ReadPreview(selfPath), PreviewNoteFor(selfPath));
            });

            if (token != _detailToken) return;
            foreach (var copy in rows) _copies.Add(copy);
            PreviewBox.Text = preview;
            PreviewNote.Text = note ?? "";
            PreviewNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
            PreviewBox.Visibility = note is null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (token != _detailToken) return;
            PreviewBox.Visibility = Visibility.Collapsed;
            PreviewNote.Visibility = Visibility.Visible;
            PreviewNote.Text = "Could not read this file: " + ex.Message;
        }
    }

    /// <summary>How much of a file the preview shows. Enough for any hand-written config; bigger
    /// files belong in the editor, not a WPF TextBox.</summary>
    private const int PreviewCharCap = 200_000;

    private static string ReadPreview(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > ConfigHubService.MaxInspectBytes) return "";
            var text = File.ReadAllText(path);
            return text.Length > PreviewCharCap
                ? text[..PreviewCharCap] + "\n\n... preview truncated - open it in the editor to see the rest."
                : text;
        }
        catch { return ""; }
    }

    private static string? PreviewNoteFor(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "This file is no longer on disk.";
            if (info.Length > ConfigHubService.MaxInspectBytes)
                return $"{ConfigHubService.FormatSize(info.Length)} - too large to preview. " +
                       "This is almost always generated data rather than something to hand-edit.";
            if (info.Length == 0) return "This file is empty.";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ── filters, sort, search ────────────────────────────────────────────────

    /// <remarks>Guarded by <see cref="_filling"/> rather than <c>IsLoaded</c>, which is already true
    /// inside the Loaded handler where the ComboBox is filled.</remarks>
    private void OnPackFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy) return;
        Refresh();
        SaveFilter();
    }

    private void OnKindChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: KindChip chip }) return;
        _kindFilter = chip.Kind;
        Refresh();
        SaveFilter();
    }

    private void OnSortButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void OnSortMenuOpened(object sender, RoutedEventArgs e)
    {
        SortNameMenuItem.IsChecked = _sort == SortMode.Name;
        SortPathMenuItem.IsChecked = _sort == SortMode.Path;
        SortModifiedMenuItem.IsChecked = _sort == SortMode.Modified;
        SortSizeMenuItem.IsChecked = _sort == SortMode.Size;
        SortDiffMenuItem.IsChecked = _sort == SortMode.Diff;
    }

    private void OnSortMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        if (Enum.TryParse<SortMode>(tag, out var mode)) _sort = mode;
        Refresh();
        SaveFilter();
    }

    private void OnDiffersOnlyClick(object sender, RoutedEventArgs e)
    {
        _differsOnly = DiffersToggle.IsChecked == true;
        Refresh();
        SaveFilter();
    }

    // ── content search ───────────────────────────────────────────────────────

    private void OnToggleContentSearch(object sender, RoutedEventArgs e)
    {
        var opening = ContentSearchBar.Visibility != Visibility.Visible;
        ContentSearchBar.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        SearchInFilesLabel.Text = opening ? "Close file search" : "Search in files";
        if (opening) { ContentQueryBox.Focus(); return; }

        if (_contentHits is null) return;
        _contentHits = null;
        Refresh();
    }

    private void OnContentQueryKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnRunContentSearch(sender, e);
    }

    private async void OnRunContentSearch(object sender, RoutedEventArgs e)
    {
        var query = ContentQueryBox.Text?.Trim() ?? "";
        if (query.Length < 2)
        {
            Say("Type at least two characters to search inside files.");
            return;
        }

        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        var ct = _workCts.Token;
        // A new search, not a refresh: the rows on screen don't answer it, so they aren't kept.
        _state.Begin($"Searching {_scan.Count:N0} file(s) for '{query}'...", refreshing: false);
        ContentSearchGoButton.Visibility = Visibility.Collapsed;
        ContentSearchCancelButton.Visibility = Visibility.Visible;

        try
        {
            var files = SelectedPackId is { } packId
                ? _scan.Where(f => f.PackId == packId).ToList()
                : _scan;
            var total = files.Count;
            var progress = new Progress<int>(done =>
                _state.Progress($"{done:N0} of {total:N0} files read"));

            var result = await Task.Run(
                () => ConfigHubService.SearchContents(files, query, maxHits: 2000, progress, ct), ct);

            _contentHits = result.Hits
                .GroupBy(h => h.File.FullPath)
                .ToDictionary(g => g.Key, g => g.ToList());

            _note =
                $"{result.Hits.Count:N0} match(es) in {_contentHits.Count:N0} file(s) " +
                $"of {result.FilesRead:N0} read" +
                (result.Skipped > 0
                    ? $" · {result.Skipped:N0} skipped (too large, unreadable, or past the 2,000-match limit)"
                    : "");
            Refresh();
        }
        catch (OperationCanceledException) { _state.Cancelled("Search cancelled."); }
        catch (Exception ex) { _state.Error("Those files could not be searched.", ex, "Search failed"); }
        finally
        {
            ContentSearchGoButton.Visibility = Visibility.Visible;
            ContentSearchCancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCancelContentSearch(object sender, RoutedEventArgs e) => _workCts?.Cancel();

    // ── list events ──────────────────────────────────────────────────────────

    private void OnFileSelected(object sender, SelectionChangedEventArgs e) => ShowDetail(SelectedRow);

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedRow is { Missing: false } row) OpenInEditor(row.Entry);
    }

    private async void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            switch (e.Key)
            {
                case Key.Enter when SelectedRow is { Missing: false } row:
                    OpenInEditor(row.Entry);
                    e.Handled = true;
                    break;
                case Key.Delete when SelectedRows().Count > 0:
                    e.Handled = true;
                    await DeleteAsync(SelectedRows());
                    break;
            }
        }
        catch (Exception ex) { Failed("That did not work.", ex); }
    }

    /// <summary>
    /// Page shortcuts: Ctrl+F opens the name filter, Ctrl+Shift+F the content search, F5 re-scans.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && shift && e.Key == Key.F)
        {
            if (ContentSearchBar.Visibility != Visibility.Visible) OnToggleContentSearch(this, e);
            ContentQueryBox.Focus();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            OnRefresh(this, e);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    private List<FileRow> SelectedRows() => FileList.SelectedItems.OfType<FileRow>().ToList();

    /// <summary>The rows an action applies to: the whole selection when the click came from the list's
    /// own selection, otherwise just the row whose button was pressed.</summary>
    private List<FileRow> RowsFor(object sender)
    {
        var row = (sender as FrameworkElement)?.Tag as FileRow
                  ?? ((sender as FrameworkElement)?.DataContext as FileRow)
                  ?? SelectedRow;
        var selection = SelectedRows();
        if (row is not null && selection.Contains(row)) return selection;
        return row is null ? selection : [row];
    }

    private FileRow? RowFor(object sender) => RowsFor(sender).FirstOrDefault();

    // ── actions ──────────────────────────────────────────────────────────────

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        ConfigHubService.InvalidateAll();
        _ = LoadAsync(force: true);
    }

    private void OnRowEdit(object sender, RoutedEventArgs e)
    {
        if (RowFor(sender) is { Missing: false } row) OpenInEditor(row.Entry);
    }

    private void OnDetailEdit(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is { Missing: false } row) OpenInEditor(row.Entry);
    }

    private void OnCopyEdit(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CopyRow copy }) OpenInEditor(copy.Entry);
    }

    /// <summary>Hands a file to the launcher's own text editor, which owns saving, encodings and
    /// unsaved-change prompts. Nothing here writes text files itself.</summary>
    private void OpenInEditor(Entry entry)
    {
        try
        {
            FileEditorWindow.OpenFileFor(_shell, entry.PackId, entry.PackName, entry.FullPath);
            HookEditorClose(entry.PackId, entry.PackName);
            Say($"Opened {entry.FileName} from {entry.PackName} in the editor.");
        }
        catch (Exception ex) { Failed("The editor could not be opened for that file.", ex); }
    }

    /// <summary>Editor windows this page has already subscribed to, so opening a second file in the
    /// same window does not stack another refresh onto its close.</summary>
    private readonly HashSet<FileEditorWindow> _hookedEditors = new();

    /// <summary>
    /// Re-reads the edited instance once its editor window closes, so its rows show the new size and
    /// age.
    /// </summary>
    /// <remarks>
    /// The remembered scan's fingerprint (per root: exists, mtime, file and folder counts, total
    /// bytes) can miss an in-place edit. The window is found by title because
    /// <see cref="FileEditorWindow"/> owns its window table; two instances with the same name only
    /// cost an extra refresh.
    /// </remarks>
    private void HookEditorClose(Guid packId, string packName)
    {
        var window = Application.Current?.Windows.OfType<FileEditorWindow>()
            .FirstOrDefault(w => w.Title == $"Edit files · {packName}");
        if (window is null || !_hookedEditors.Add(window)) return;

        window.Closed += OnEditorClosed;

        void OnEditorClosed(object? sender, EventArgs e)
        {
            window.Closed -= OnEditorClosed;
            _hookedEditors.Remove(window);
            // Drop the cached walk either way so a later visit doesn't read it back.
            ConfigHubService.Invalidate(packId);
            // force: false so only the invalidated instance is re-walked; the others stay cached.
            if (IsLoaded) _ = LoadAsync(force: false);
        }
    }

    private void OnRowReveal(object sender, RoutedEventArgs e) => Reveal(RowFor(sender)?.Entry);
    private void OnDetailReveal(object sender, RoutedEventArgs e) => Reveal(SelectedRow?.Entry);

    private void Reveal(Entry? entry)
    {
        if (entry is null || entry.FullPath.Length == 0) return;
        if (!SafeLaunch.RevealFile(entry.FullPath)) Say("Explorer could not be opened at that file.");
    }

    private void OnRowCopyPath(object sender, RoutedEventArgs e)
    {
        var rows = RowsFor(sender);
        if (rows.Count == 0) return;
        var text = string.Join(Environment.NewLine, rows.Select(r => r.Entry.FullPath));
        Say(ClipboardHelper.TrySetText(text)
            ? $"Copied {rows.Count} path(s) to the clipboard."
            : "The clipboard is locked by another program - try again in a moment.");
    }

    private void OnDetailMore(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void OnRowPin(object sender, RoutedEventArgs e)
    {
        var rows = RowsFor(sender);
        if (rows.Count == 0) return;
        try
        {
            var pins = App.State.Settings.ConfigHubPins;
            // A mixed selection pins everything rather than toggling each row.
            var pinAll = rows.Any(r => !r.Pinned);
            foreach (var row in rows)
            {
                var key = ConfigHubService.PinKey(row.PackId, row.RelativePath);
                if (pinAll) { if (!pins.Contains(key)) pins.Add(key); }
                else pins.Remove(key);
            }
            App.State.Settings.Save();
            // Refresh first: it resets the note line, so Say() has to come after it.
            Refresh();
            Say(pinAll
                ? $"Pinned {rows.Count} file(s) to the top of the list."
                : $"Unpinned {rows.Count} file(s).");
        }
        catch (Exception ex) { Failed("That pin could not be saved.", ex); }
    }

    // ── compare ──────────────────────────────────────────────────────────────

    private async void OnRowCompare(object sender, RoutedEventArgs e)
    {
        try { await CompareAsync(RowFor(sender), null); }
        catch (Exception ex) { Failed("Those copies could not be compared.", ex); }
    }

    private async void OnDetailCompare(object sender, RoutedEventArgs e)
    {
        try { await CompareAsync(SelectedRow, null); }
        catch (Exception ex) { Failed("Those copies could not be compared.", ex); }
    }

    private async void OnCopyCompare(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement { Tag: CopyRow copy })
                await CompareAsync(SelectedRow, copy.Entry);
        }
        catch (Exception ex) { Failed("Those copies could not be compared.", ex); }
    }

    private async Task CompareAsync(FileRow? row, Entry? against)
    {
        if (row is null || row.Missing) return;
        if (row.Copies.Count < 2)
        {
            await AppDialog.MessageAsync(_shell, "Nothing to compare",
                $"Only {row.Entry.PackName} has a file at {row.RelativePath}. " +
                "Use 'Copy to other instances' to put it somewhere else first.");
            return;
        }

        var card = new ConfigCompareCard(row.Copies, row.Entry, against, _shell);
        await _shell.ShowCardAsync(card, card.Completion, card.Close);

        if (card.CopiedSomething)
        {
            ConfigHubService.InvalidateAll();
            await LoadAsync(force: true);
        }
    }

    // ── copy across instances ────────────────────────────────────────────────

    private async void OnRowCopyTo(object sender, RoutedEventArgs e)
    {
        try { await CopyAsync(RowsFor(sender), wholeFolder: false); }
        catch (Exception ex) { Failed("Those files could not be copied.", ex); }
    }

    private async void OnDetailCopyTo(object sender, RoutedEventArgs e)
    {
        try { await CopyAsync(SelectedRows(), wholeFolder: false); }
        catch (Exception ex) { Failed("Those files could not be copied.", ex); }
    }

    private async void OnRowCopyFolder(object sender, RoutedEventArgs e)
    {
        try { await CopyAsync(RowsFor(sender), wholeFolder: true); }
        catch (Exception ex) { Failed("Those files could not be copied.", ex); }
    }

    private async Task CopyAsync(List<FileRow> rows, bool wholeFolder)
    {
        rows = rows.Where(r => !r.Missing).ToList();
        if (rows.Count == 0) return;

        var sourcePackId = rows[0].PackId;
        if (rows.Any(r => r.PackId != sourcePackId))
        {
            await AppDialog.MessageAsync(_shell, "One instance at a time",
                "The selected files come from different instances, so there is no single source to copy " +
                "from. Pick an instance in the filter first, then select the files.");
            return;
        }

        var paths = new List<string>();
        if (wholeFolder)
        {
            foreach (var folder in rows.Select(r => r.Entry.FolderPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (folder.Length == 0) continue;
                paths.AddRange(ConfigHubService.ExpandFolder(_scan, sourcePackId, folder));
            }
            paths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0)
            {
                Say("That file is not inside a folder that can be copied whole.");
                return;
            }
        }
        else
        {
            paths = rows.Select(r => r.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var targets = _packs.Where(p => p.Id != sourcePackId).ToList();
        if (targets.Count == 0)
        {
            await AppDialog.MessageAsync(_shell, "Nowhere to copy to",
                "There is only one instance, so there is no other instance to copy these files into.");
            return;
        }

        var card = new ConfigCopyCard(PackName(sourcePackId), paths, targets, _shell);
        await _shell.ShowCardAsync(card, card.Completion, card.Close);
        if (card.ChosenTargets is not { Count: > 0 } chosen) return;

        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        var ct = _workCts.Token;

        var sourceGameDir = App.State.Packs.GameDir(sourcePackId);
        var preview = card.Preview;
        var shared = preview?.SharedPacks.Where(name => chosen.Any(t => t.Name == name)).ToList() ?? [];
        var overwriting = preview?.Items.Count(i => i.Exists && chosen.Any(t => t.Id == i.PackId)) ?? 0;

        var message =
            $"Copy {paths.Count} file(s) from {PackName(sourcePackId)} into " +
            $"{chosen.Count} other instance(s).\n\n" +
            (overwriting > 0
                ? $"{overwriting} existing file(s) will be replaced. Each one is backed up next to itself " +
                  "as .bak-<timestamp> first.\n\n"
                : "Nothing existing will be replaced.\n\n") +
            (shared.Count > 0
                ? $"Careful: in {string.Join(", ", shared)} these paths are marked Shared, so the change " +
                  "will travel to everyone else on those packs the next time they sync.\n\n"
                : "") +
            "Continue?";

        if (!await AppDialog.ConfirmAsync(_shell, "Copy across instances", message, "Copy", "Cancel",
                danger: shared.Count > 0))
            return;

        _state.Begin("Copying...", refreshing: true);
        try
        {
            var progress = new Progress<string>(what => _state.Progress("Copying " + what));
            var result = await Task.Run(() => ConfigHubService.Copy(
                sourceGameDir, paths, chosen, App.State.Packs, progress, ct), ct);

            var copiedNote =
                $"Copied {result.Copied} file(s) into {chosen.Count} instance(s)" +
                (result.BackedUp > 0 ? $" · {result.BackedUp} replaced file(s) backed up" : "") +
                (result.Failures.Count > 0 ? $" · {result.Failures.Count} failed" : "") + ".";

            if (result.Failures.Count > 0)
                await AppDialog.MessageAsync(_shell, "Some files did not copy",
                    string.Join("\n", result.Failures.Take(12)) +
                    (result.Failures.Count > 12 ? $"\n... and {result.Failures.Count - 12} more." : ""));

            await LoadAsync(force: true, note: copiedNote);
        }
        catch (OperationCanceledException)
        {
            _state.Cancelled("Copy cancelled - files already written were left in place and backed up.");
        }
        catch (Exception ex)
        {
            // The page is mid-refresh; letting this escape would leave the busy bar running.
            _state.Error("Those files could not all be copied.", ex, "Copy failed");
        }
    }

    // ── edits and defaultconfigs ─────────────────────────────────────────────

    private void OnEditedChipClick(object sender, MouseButtonEventArgs e)
    {
        _editedOnly = !_editedOnly;
        Refresh();
        if (_scope is null) SaveFilter();
    }

    private async void OnRowMarkNotEdited(object sender, RoutedEventArgs e)
    {
        try { await MarkNotEditedAsync(RowsFor(sender)); }
        catch (Exception ex) { Failed("Those files could not be marked as not edited.", ex); }
    }

    private async void OnDetailMarkNotEdited(object sender, RoutedEventArgs e)
    {
        try { await MarkNotEditedAsync(SelectedRows()); }
        catch (Exception ex) { Failed("That file could not be marked as not edited.", ex); }
    }

    /// <summary>Takes each file as it is now as its new original, so it stops showing as
    /// edited.</summary>
    private async Task MarkNotEditedAsync(List<FileRow> rows)
    {
        var entries = rows.Where(r => r.IsEdited).Select(r => r.Entry).ToList();
        if (entries.Count == 0)
        {
            Say("None of the selected files is marked as edited.");
            return;
        }
        await Task.Run(() =>
        {
            foreach (var pack in entries.GroupBy(e => e.PackId))
                ConfigEditTracker.MarkNotEdited(App.State.Packs.PackRoot(pack.Key), pack);
        });
        // Not forced: nothing on disk moved, so only the edit answers need recomputing.
        await LoadAsync(force: false, note: $"Marked {entries.Count} file(s) as not edited.");
    }

    private async void OnRowCompareDefaults(object sender, RoutedEventArgs e)
    {
        try { await CompareWithDefaultsAsync(RowFor(sender)); }
        catch (Exception ex) { Failed("Those files could not be compared.", ex); }
    }

    private async void OnDetailCompareDefaults(object sender, RoutedEventArgs e)
    {
        try { await CompareWithDefaultsAsync(SelectedRow); }
        catch (Exception ex) { Failed("Those files could not be compared.", ex); }
    }

    /// <summary>
    /// Compares a config with its defaultconfigs copy in the same instance, in the cross-instance
    /// compare card.
    /// </summary>
    /// <remarks>
    /// The card names each side by its instance; here both sides are one instance, so each is named by
    /// its folder instead (<c>config</c>, <c>defaultconfigs</c>), which is what its copy buttons and
    /// its "replace" prompt then say. The card backs up whatever it overwrites.
    /// </remarks>
    private async Task CompareWithDefaultsAsync(FileRow? row)
    {
        if (row is null || row.Missing) return;
        if (DefaultsCounterpart(row.Entry) is not { } other)
        {
            var target = ConfigHubService.DefaultConfigsPath(row.Entry);
            await AppDialog.MessageAsync(_shell, "Nothing to compare",
                row.Entry.Kind == Kind.DefaultConfigs
                    ? $"{row.Entry.PackName} has no config in use that {row.RelativePath} fills in."
                    : target is not null
                        ? $"There is no {target} in {row.Entry.PackName} yet. Use 'Copy to defaultconfigs' to put this file there."
                        : $"FML never reads {row.FileName} from defaultconfigs: {ConfigHubService.WhyNotDefaultConfigs(row.Entry)}.");
            return;
        }

        var inUse = row.Entry.Kind == Kind.DefaultConfigs ? other : row.Entry;
        var defaults = row.Entry.Kind == Kind.DefaultConfigs ? row.Entry : other;
        // "config/create/client.toml" against "defaultconfigs/create/client.toml": the in-use side is
        // named by what is left of its path once the shared name is taken off, "config".
        var sharedName = defaults.RelativePath["defaultconfigs/".Length..];
        var left = inUse with { PackName = inUse.RelativePath[..^(sharedName.Length + 1)] };
        var right = defaults with { PackName = "defaultconfigs" };

        var card = new ConfigCompareCard([left, right], left, right, _shell);
        await _shell.ShowCardAsync(card, card.Completion, card.Close);
        if (card.CopiedSomething)
        {
            ConfigHubService.Invalidate(row.PackId);
            await LoadAsync(force: true);
        }
    }

    private void OnDefaultsMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    /// <summary>Labels the defaultconfigs menu with what each entry would act on right now.</summary>
    private void OnDefaultsMenuOpened(object sender, RoutedEventArgs e)
    {
        var selected = SelectedRows().Count(r => !r.Missing);
        DefaultsCopySelectedItem.Header = selected > 0
            ? $"Copy the {selected} selected file(s)"
            : "Copy the selected files (select some first)";
        DefaultsMoveSelectedItem.Header = selected > 0
            ? $"Move the {selected} selected file(s) - take them out of config"
            : "Move the selected files - take them out of config";
        DefaultsCopySelectedItem.IsEnabled = DefaultsMoveSelectedItem.IsEnabled = selected > 0;

        var edited = EditedNotInDefaults();
        DefaultsCopyEditedItem.Header = SelectedPackId is null
            ? "Copy every edited config (pick an instance first)"
            : $"Copy every edited config not there yet ({edited.Count})";
        DefaultsCopyEditedItem.IsEnabled = edited.Count > 0;
    }

    /// <summary>The listed instance's edited configs that FML could read from defaultconfigs and that
    /// have no identical copy there yet. Empty with "All instances": one instance at a time.</summary>
    private List<Entry> EditedNotInDefaults()
    {
        if (SelectedPackId is not { } packId) return [];
        return _scan.Where(e => e.PackId == packId
                                && _edits.For(e) is { Edited: true, DefaultPath: not null } edit
                                && edit.DefaultState != DefaultCopyState.Same)
                    .ToList();
    }

    private async void OnCopySelectedToDefaults(object sender, RoutedEventArgs e)
    {
        try { await CopyToDefaultsAsync(SelectedRows(), removeOriginal: false); }
        catch (Exception ex) { Failed("Those files could not be copied into defaultconfigs.", ex); }
    }

    private async void OnMoveSelectedToDefaults(object sender, RoutedEventArgs e)
    {
        try { await CopyToDefaultsAsync(SelectedRows(), removeOriginal: true); }
        catch (Exception ex) { Failed("Those files could not be moved into defaultconfigs.", ex); }
    }

    private async void OnCopyEditedToDefaults(object sender, RoutedEventArgs e)
    {
        try { await CopyToDefaultsAsync(EditedNotInDefaults(), removeOriginal: false); }
        catch (Exception ex) { Failed("Those files could not be copied into defaultconfigs.", ex); }
    }

    private async void OnRowCopyToDefaults(object sender, RoutedEventArgs e)
    {
        try { await CopyToDefaultsAsync(RowsFor(sender), removeOriginal: false); }
        catch (Exception ex) { Failed("Those files could not be copied into defaultconfigs.", ex); }
    }

    private async void OnDetailCopyToDefaults(object sender, RoutedEventArgs e)
    {
        try { await CopyToDefaultsAsync(SelectedRows(), removeOriginal: false); }
        catch (Exception ex) { Failed("That file could not be copied into defaultconfigs.", ex); }
    }

    private async void OnDetailMoveToDefaults(object sender, RoutedEventArgs e)
    {
        try { await CopyToDefaultsAsync(SelectedRows(), removeOriginal: true); }
        catch (Exception ex) { Failed("That file could not be moved into defaultconfigs.", ex); }
    }

    private Task CopyToDefaultsAsync(List<FileRow> rows, bool removeOriginal) =>
        CopyToDefaultsAsync(rows.Where(r => !r.Missing).Select(r => r.Entry).ToList(), removeOriginal);

    /// <summary>
    /// Copies configs into the instance's defaultconfigs, or moves them there with
    /// <paramref name="removeOriginal"/>, after saying exactly what will happen.
    /// </summary>
    /// <remarks>
    /// <para>Copy is the default because it changes nothing about how the instance plays: FML only
    /// reads defaultconfigs for a config that is missing. A move deletes the original so FML
    /// recreates it from the new default on the next start.</para>
    /// <para>A copy that replaces a different defaultconfigs file backs it up first, as every other
    /// overwrite on this page does, and the question says so. Files FML would never read from there
    /// are named with the reason instead of being copied.</para>
    /// </remarks>
    private async Task CopyToDefaultsAsync(List<Entry> entries, bool removeOriginal)
    {
        if (entries.Count == 0)
        {
            Say("Select the configs to put into defaultconfigs first.");
            return;
        }
        var packId = entries[0].PackId;
        if (entries.Any(e => e.PackId != packId))
        {
            await AppDialog.MessageAsync(_shell, "One instance at a time",
                "The selected files come from different instances. Pick an instance in the filter first, " +
                "then select its files.");
            return;
        }
        var pack = _packs.FirstOrDefault(p => p.Id == packId);
        if (pack is not null && ConfigHubService.NoDefaultConfigsReason(pack.Loader) is { } noDefaults)
        {
            await AppDialog.MessageAsync(_shell, "No defaultconfigs here", $"{pack.Name}: {noDefaults}");
            return;
        }

        var skipped = entries.Where(e => ConfigHubService.DefaultConfigsPath(e) is null).ToList();
        var gameDir = App.State.Packs.GameDir(packId);
        var items = await Task.Run(() => ConfigHubService.PreviewDefaults(gameDir, entries.Except(skipped)));
        var todo = removeOriginal ? items : items.Where(i => !i.Identical).ToList();
        var skippedText = skipped.Count == 0
            ? ""
            : $"Left out {skipped.Count}:\n" +
              string.Join("\n", skipped.Take(6).Select(s => $"· {s.RelativePath} - {ConfigHubService.WhyNotDefaultConfigs(s)}")) +
              (skipped.Count > 6 ? $"\n... and {skipped.Count - 6} more." : "");
        if (todo.Count == 0)
        {
            await AppDialog.MessageAsync(_shell, "Nothing to copy",
                items.Count > 0
                    ? "Every selected config already has an identical copy in defaultconfigs."
                    : "None of the selected files is a config FML reads from defaultconfigs." +
                      (skippedText.Length > 0 ? "\n\n" + skippedText : ""));
            return;
        }

        var replacing = todo.Count(i => i.Exists && !i.Identical);
        var verb = removeOriginal ? "Move" : "Copy";
        var name = PackName(packId);
        var message =
            $"{verb} {todo.Count} config(s) into defaultconfigs in {name}:\n" +
            string.Join("\n", todo.Take(8).Select(i => $"· {i.Source.RelativePath}  ->  {i.TargetRelativePath}")) +
            (todo.Count > 8 ? $"\n... and {todo.Count - 8} more." : "") + "\n\n" +
            (replacing > 0
                ? $"{replacing} of them already have a different copy there. It is replaced, and backed up " +
                  "next to itself as .bak-<timestamp> first.\n\n"
                : "") +
            (removeOriginal
                ? "The originals are deleted. FML copies each one back from defaultconfigs the next time the " +
                  "game starts, or for a world's server config, the next time that world loads." +
                  (pack?.Loader == LoaderKind.NeoForge && todo.Any(i => i.Source.Kind == Kind.ServerConfig)
                      ? " On NeoForge a world without its own server config uses the one in config first." : "") +
                  "\n\n"
                : "The originals stay where they are, so this instance plays exactly as it does now. FML only " +
                  "reads defaultconfigs for a config that is missing: a new install, or a new world.\n\n") +
            SharingNote(packId) +
            (skippedText.Length > 0 ? skippedText + "\n\n" : "") +
            "Continue?";

        if (!await AppDialog.ConfirmAsync(_shell, $"{verb} to defaultconfigs", message, verb, "Cancel",
                danger: removeOriginal || replacing > 0))
            return;

        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        var ct = _workCts.Token;
        _state.Begin("Copying into defaultconfigs...", refreshing: true);
        try
        {
            var packRoot = App.State.Packs.PackRoot(packId);
            var result = await Task.Run(() =>
            {
                var copied = ConfigHubService.CopyToDefaults(packId, gameDir, todo, removeOriginal, ct);
                // What was just written is the chosen default, not an edit to the old one.
                ConfigEditTracker.Accept(packRoot, todo.Select(i => (i.TargetRelativePath,
                    Path.Combine(gameDir, i.TargetRelativePath.Replace('/', Path.DirectorySeparatorChar)))));
                return copied;
            }, ct);
            var done =
                $"{(removeOriginal ? "Moved" : "Copied")} {todo.Count - result.Failures.Count} config(s) into defaultconfigs" +
                (result.BackedUp > 0 ? $" · {result.BackedUp} replaced file(s) backed up" : "") +
                (result.Failures.Count > 0 ? $" · {result.Failures.Count} failed" : "") + ".";
            if (result.Failures.Count > 0)
                await AppDialog.MessageAsync(_shell, "Some files did not copy",
                    string.Join("\n", result.Failures.Take(12)) +
                    (result.Failures.Count > 12 ? $"\n... and {result.Failures.Count - 12} more." : ""));
            await LoadAsync(force: true, note: done);
        }
        catch (OperationCanceledException)
        {
            _state.Cancelled("Stopped - files already copied were left in place.");
        }
        catch (Exception ex)
        {
            _state.Error("Those files could not all be copied into defaultconfigs.", ex, "Copy failed");
        }
    }

    /// <summary>
    /// A warning for the defaultconfigs question when this instance's sync would carry config but not
    /// defaultconfigs, so its collaborators would never get the new defaults. Empty otherwise.
    /// </summary>
    /// <remarks>Instances made before the default rules shared defaultconfigs, or with rules of their
    /// own, can be in this state. Local instances never sync, so they get no warning.</remarks>
    private static string SharingNote(Guid packId)
    {
        try
        {
            if (App.State.LocalPacks.Contains(packId)) return "";
            var rules = App.State.Rules.Load(App.State.Packs.PackRoot(packId));
            var configShared = App.State.Rules.Match("config/x.toml", rules).IsAutoShared;
            var defaultsShared = App.State.Rules.Match("defaultconfigs/x.toml", rules).IsAutoShared;
            return configShared && !defaultsShared
                ? "Note: this instance's file rules share config but not defaultconfigs, so people you share " +
                  "it with won't get these copies. Add a Shared rule for defaultconfigs/ in its file rules.\n\n"
                : "";
        }
        catch { return ""; }
    }

    // ── housekeeping: new, rename, delete, restore ───────────────────────────

    private async void OnNewFile(object sender, RoutedEventArgs e)
    {
        try
        {
            if (TargetPackId is not { } packId)
            {
                Say("There is no instance to create a file in.");
                return;
            }

            var seed = SelectedRow is { Missing: false } row ? row.Entry.FolderPath + "/" : "config/";
            var entered = await _shell.PromptAsync("New config file",
                "Path inside the instance's game folder - e.g. kubejs/server_scripts/recipes.js. " +
                "End it with a / to create an empty folder instead.", seed);
            if (string.IsNullOrWhiteSpace(entered)) return;

            var rel = entered.Trim().Replace('\\', '/').TrimStart('/');
            var gameDir = App.State.Packs.GameDir(packId);
            // Drive letters, "..", device names and the like would land outside the game folder.
            if (PathSafety.ResolveInside(gameDir, rel.TrimEnd('/')) is not { } full)
            {
                Say("That path steps outside the instance folder or uses a name Windows does not allow.");
                return;
            }

            if (rel.EndsWith('/'))
            {
                Directory.CreateDirectory(full);
                Say($"Created folder {rel} in {PackName(packId)}.");
            }
            else if (File.Exists(full))
            {
                Say($"{rel} already exists in {PackName(packId)}.");
                return;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, "");
                // No mod wrote it, so it is the user's edit from the start.
                if (ConfigHubService.KindForPath(rel) is { } kind && ConfigEditTracker.IsTracked(kind))
                    ConfigEditTracker.MarkCreated(App.State.Packs.PackRoot(packId), rel);
                Say($"Created {rel} in {PackName(packId)}.");
                FileEditorWindow.OpenFileFor(_shell, packId, PackName(packId), full);
            }

            ConfigHubService.Invalidate(packId);
            await LoadAsync(force: true);
        }
        catch (Exception ex) { Failed("That file or folder could not be created.", ex); }
    }

    private async void OnRowRename(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFor(sender) is not { Missing: false } row) return;
            var entered = await _shell.PromptAsync("Rename file",
                $"New name for {row.FileName} (it stays in {row.Entry.FolderPath}).", row.FileName);
            if (string.IsNullOrWhiteSpace(entered) || entered == row.FileName) return;

            var name = entered.Trim();
            var dir = Path.GetDirectoryName(row.Entry.FullPath)!;
            if (PathSafety.ResolveFileName(dir, name) is not { } dest)
            {
                Say("Windows does not allow that as a file name.");
                return;
            }

            if (File.Exists(dest))
            {
                Say($"{name} already exists in that folder.");
                return;
            }

            File.Move(row.Entry.FullPath, dest);
            ConfigHubService.Invalidate(row.PackId);
            await LoadAsync(force: true,
                note: $"Renamed {row.FileName} to {name}. " +
                      "Remember that most mods look for an exact config file name.");
        }
        catch (Exception ex) { Failed("That file could not be renamed.", ex); }
    }

    private async void OnRowDelete(object sender, RoutedEventArgs e)
    {
        try { await DeleteAsync(RowsFor(sender)); }
        catch (Exception ex) { Failed("That file could not be deleted.", ex); }
    }

    private async Task DeleteAsync(List<FileRow> rows)
    {
        // A pinned row whose file is gone is removed from the pin list rather than from disk.
        var ghosts = rows.Where(r => r.Missing).ToList();
        rows = rows.Where(r => !r.Missing).ToList();
        if (ghosts.Count > 0)
        {
            foreach (var ghost in ghosts)
                App.State.Settings.ConfigHubPins.Remove(
                    ConfigHubService.PinKey(ghost.PackId, ghost.RelativePath));
            App.State.Settings.Save();
            Refresh();
            Say($"Removed {ghosts.Count} pin(s) whose file no longer exists.");
        }
        if (rows.Count == 0) return;

        var what = rows.Count == 1
            ? $"Delete {rows[0].RelativePath} from {rows[0].Entry.PackName}?"
            : $"Delete {rows.Count} files?";
        var body = what + "\n\n" +
                   (rows.Count > 1
                       ? string.Join("\n", rows.Take(8).Select(r => $"· {r.RelativePath} - {r.Entry.PackName}"))
                         + (rows.Count > 8 ? $"\n... and {rows.Count - 8} more." : "") + "\n\n"
                       : "") +
                   "A mod usually rewrites a missing config with its defaults on the next launch, but any " +
                   "settings in it are lost. This cannot be undone from here.";

        if (!await AppDialog.ConfirmAsync(_shell, "Delete file", body, "Delete", "Cancel", danger: true))
            return;

        var deleted = 0;
        var failures = new List<string>();
        foreach (var row in rows)
        {
            try { File.Delete(row.Entry.FullPath); deleted++; ConfigHubService.Invalidate(row.PackId); }
            catch (Exception ex) { failures.Add($"{row.RelativePath}: {ex.Message}"); }
        }

        var deletedNote = $"Deleted {deleted} file(s)." +
                          (failures.Count > 0 ? $" {failures.Count} could not be deleted." : "");
        if (failures.Count > 0)
            await AppDialog.MessageAsync(_shell, "Some files were not deleted",
                string.Join("\n", failures.Take(10)));
        await LoadAsync(force: true, note: deletedNote);
    }

    private async void OnRowRestoreBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RowFor(sender) is not { Missing: false } row) return;
            var backups = await Task.Run(() => ConfigHubService.FindBackups(row.Entry.FullPath));
            if (backups.Count == 0)
            {
                await AppDialog.MessageAsync(_shell, "No backups",
                    $"There is no backup of {row.FileName} in {row.Entry.PackName}. " +
                    "Backups are written here whenever this page overwrites a file.");
                return;
            }

            // Most recently taken, not newest by mtime: a backup keeps its source file's last-write time.
            var newest = backups[0];
            if (!await AppDialog.ConfirmAsync(_shell, "Restore backup",
                    $"Restore {row.FileName} in {row.Entry.PackName} from the backup taken " +
                    $"{ConfigHubService.FormatAge(ConfigHubService.BackupTakenUtc(newest))} " +
                    $"({ConfigHubService.FormatSize(newest.Length)})?\n\n" +
                    (backups.Count > 1 ? $"There are {backups.Count} backups; this restores the newest.\n\n" : "") +
                    "The file as it stands now is backed up first, so this is reversible.",
                    "Restore", "Cancel"))
                return;

            var path = row.Entry.FullPath;
            var source = newest.FullName;
            await Task.Run(() =>
            {
                // Invariant: BackupTakenUtc reads this stamp back to order the backups.
                File.Copy(path, $"{path}.bak-{TimeFormat.StampNow()}", overwrite: true);
                File.Copy(source, path, overwrite: true);
            });
            ConfigHubService.Invalidate(row.PackId);
            await LoadAsync(force: true,
                note: $"Restored {row.FileName} in {row.Entry.PackName}.");
        }
        catch (Exception ex) { Failed("That backup could not be restored.", ex); }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (TargetPackId is not { } packId) { Say("There is no instance to open."); return; }
        try
        {
            var dir = Path.Combine(App.State.Packs.GameDir(packId), "config");
            if (!Directory.Exists(dir)) dir = App.State.Packs.GameDir(packId);
            if (!Directory.Exists(dir))
            {
                Say($"{PackName(packId)} has no game folder yet - launch or sync it once.");
                return;
            }
            if (!SafeLaunch.OpenFolder(dir)) Say("That folder could not be opened in Explorer.");
        }
        catch (Exception ex) { Failed("That folder could not be opened in Explorer.", ex); }
    }

    // ── odds and ends ────────────────────────────────────────────────────────

    /// <summary>A one-line note under the list about what just happened. The next rebuild replaces
    /// it with the standing note.</summary>
    private void Say(string text) => _state.Note(text);

    /// <summary>Reports a failed user action: a plain sentence on screen, the exception to the
    /// launcher log.</summary>
    private void Failed(string plain, Exception ex)
    {
        AppLog.LogError(nameof(ConfigHubView), ex);
        Say(plain);
    }

    private Guid? _restoredPackId;

    /// <summary>
    /// Restores the filter the page was left on, stored as
    /// <c>kind|packId|sort|differsOnly|editedOnly</c>. Anything that doesn't parse falls back to the
    /// default. An instance's Configs tab neither reads nor writes it.
    /// </summary>
    private void RestoreFilter()
    {
        var saved = App.State.Settings.ConfigHubLastFilter;
        if (string.IsNullOrWhiteSpace(saved)) return;
        var parts = saved.Split('|');
        if (parts.Length > 0 && Enum.TryParse<Kind>(parts[0], out var kind)) _kindFilter = kind;
        if (parts.Length > 1 && Guid.TryParseExact(parts[1], "N", out var packId)) _restoredPackId = packId;
        if (parts.Length > 2 && Enum.TryParse<SortMode>(parts[2], out var sort)) _sort = sort;
        if (parts.Length > 3) _differsOnly = parts[3] == "1";
        if (parts.Length > 4) _editedOnly = parts[4] == "1";
    }

    private void SaveFilter()
    {
        if (_scope is not null) return;
        try
        {
            App.State.Settings.ConfigHubLastFilter =
                $"{(_kindFilter is { } k ? k.ToString() : "")}|" +
                $"{(SelectedPackId is { } id ? id.ToString("N") : "")}|" +
                $"{_sort}|{(_differsOnly ? "1" : "0")}|{(_editedOnly ? "1" : "0")}";
            App.State.Settings.Save();
        }
        catch { /* a settings write that fails must not break the filter the user just clicked */ }
    }

    // ── row models ───────────────────────────────────────────────────────────

    private sealed record PackFilterItem(Guid? Id, string Label);

    /// <summary>One chip in the kind strip. A null count means not counted yet and shows as a
    /// dash.</summary>
    private sealed class KindChip(Kind? kind, string label, int? count, bool selected, string hint)
    {
        public Kind? Kind { get; } = kind;
        public string Label { get; } = label;
        public string CountLabel { get; } = count is { } n ? n.ToString("N0") : "-";
        public bool IsSelected { get; } = selected;
        public string Hint { get; } = hint;
    }

    /// <summary>
    /// One row: a game-relative path, the instance whose copy this row shows, and every instance that
    /// has a file at the same path.
    /// </summary>
    /// <remarks>
    /// Labels are computed once here since the list is rebuilt on every filter change. No Brush is
    /// stored: the XAML colours rows from <see cref="StateName"/> with DynamicResource, so theme
    /// changes repaint them.
    /// </remarks>
    private sealed class FileRow
    {
        public FileRow(Entry entry, IReadOnlyList<Entry> copies, GroupState state,
                       bool pinned, int errorCount, int hitCount, bool missing = false, FileEdit? edit = null)
        {
            Entry = entry;
            Copies = copies;
            State = state;
            Pinned = pinned;
            Missing = missing;
            Edit = edit;

            var differing = state == GroupState.Differs ? " · differs somewhere" : "";
            var where = missing
                ? "file no longer on disk"
                : copies.Count > 1
                    ? $"in {copies.Count} instances{differing}"
                    : $"only in {entry.PackName}";

            MetaLabel = missing
                ? $"{entry.RelativePath} · {where}"
                : hitCount > 0
                    ? $"{entry.RelativePath} · {hitCount} match(es) · {where}"
                    : $"{entry.RelativePath} · {ConfigHubService.FormatSize(entry.Size)} · " +
                      $"{ConfigHubService.FormatAge(entry.ModifiedUtc)} · {where}";

            FullPathHint = missing ? entry.RelativePath : entry.FullPath;
            ErrorVisibility = errorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            ErrorHint = errorCount > 0
                ? $"KubeJS logged {errorCount} error line(s) naming this script - open it to see them."
                : "";
        }

        public Entry Entry { get; }
        public IReadOnlyList<Entry> Copies { get; }
        public GroupState State { get; }
        public bool Pinned { get; }
        public bool Missing { get; }

        public Guid PackId => Entry.PackId;
        public string RelativePath => Entry.RelativePath;
        public string FileName => Entry.FileName;
        public string MetaLabel { get; }
        public string FullPathHint { get; }

        public string Glyph => Entry.Kind switch
        {
            Kind.KubeJs => "",      // code
            Kind.KubeJsLog => "",   // warning-ish: a log is only read when something went wrong
            _ => ""                 // document
        };

        public double MissingOpacity => Missing ? 0.45 : 1.0;

        /// <summary>The value the XAML's DataTriggers switch on. Kept as a string rather than the enum
        /// so the trigger comparison works without a converter.</summary>
        public string StateName => State.ToString();

        public string StateLabel => State switch
        {
            GroupState.Differs => "DIFFERS",
            GroupState.Identical => "SAME",
            GroupState.Unknown => "?",
            _ => "ONLY HERE"
        };

        public Visibility StateVisibility =>
            Missing || State == GroupState.Unique ? Visibility.Collapsed : Visibility.Visible;

        public string StateHint => State switch
        {
            GroupState.Differs =>
                $"{Copies.Count} instances have a file here and at least one differs. Compare them to see how.",
            GroupState.Identical => $"All {Copies.Count} instances have exactly the same file here.",
            GroupState.Unknown => "Too large to compare, or locked by a running game.",
            _ => $"Only {Entry.PackName} has a file at this path."
        };

        public Visibility CompareVisibility =>
            Copies.Count > 1 && !Missing ? Visibility.Visible : Visibility.Collapsed;

        public string CompareHint => $"Compare this file across {Copies.Count} instances";

        public Visibility ErrorVisibility { get; }
        public string ErrorHint { get; }

        public string PinGlyph => Pinned ? "" : "";
        public string PinHint => Pinned ? "Unpin" : "Pin to the top of the list";

        /// <summary>Edit tracking's answer for this file, or null when it isn't tracked or hasn't been
        /// checked yet.</summary>
        public FileEdit? Edit { get; }

        public bool IsEdited => Edit?.Edited == true && !Missing;

        public Visibility EditedVisibility => IsEdited ? Visibility.Visible : Visibility.Collapsed;

        public string EditedHint => Edit is { Created: true }
            ? "Created in the launcher, so no mod wrote it."
            : Edit?.FirstSeenUtc is { } seen
                ? $"Changed since the launcher first saw it, {TimeFormat.DateTime(seen)}."
                : "Changed since the launcher first saw it.";

        private DefaultCopyState DefaultState => Edit?.DefaultState ?? DefaultCopyState.NotApplicable;

        /// <summary>The value the defaults badge's DataTriggers switch on.</summary>
        public string DefaultsStateName => DefaultState.ToString();

        public Visibility DefaultsVisibility =>
            !Missing && DefaultState is DefaultCopyState.Same or DefaultCopyState.Differs or DefaultCopyState.Unknown
                ? Visibility.Visible
                : Visibility.Collapsed;

        public string DefaultsLabel => DefaultState switch
        {
            DefaultCopyState.Same => "IN DEFAULTS",
            DefaultCopyState.Differs => "DEFAULTS DIFFER",
            _ => "IN DEFAULTS ?"
        };

        public string DefaultsHint => DefaultState switch
        {
            DefaultCopyState.Same => $"{Edit?.DefaultPath} is identical to this file.",
            DefaultCopyState.Differs =>
                $"{Edit?.DefaultPath} holds a different version. Compare them, or copy this one over it.",
            _ => $"{Edit?.DefaultPath} exists but could not be read to compare."
        };
    }

    /// <summary>One instance's copy of the selected file, in the detail pane.</summary>
    private sealed class CopyRow(Entry entry, bool isSelf, bool? same)
    {
        public Entry Entry { get; } = entry;
        public string PackName => Entry.PackName;

        public string MetaLabel =>
            $"{ConfigHubService.FormatSize(Entry.Size)} · modified {ConfigHubService.FormatAge(Entry.ModifiedUtc)}";

        public string StateLabel => isSelf ? "THIS ONE" : same switch
        {
            true => "SAME",
            false => "DIFFERS",
            _ => "?"
        };

        public string StateHint => isSelf
            ? "The copy shown above."
            : same switch
            {
                true => "Byte-for-byte identical to the copy shown above.",
                false => "Differs from the copy shown above - compare to see how.",
                _ => "Could not be read to compare: too large, or locked by a running game."
            };

        public Visibility CompareVisibility => isSelf ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>One content-search hit inside the selected file.</summary>
    private sealed class HitRow(ConfigHubService.SearchHit hit)
    {
        public string LineLabel { get; } = $"L{hit.LineNumber}";
        public string Line { get; } = hit.Line;
    }
}
