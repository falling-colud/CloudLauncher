using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using Entry = CloudLauncher.Services.ConfigHubService.Entry;

namespace CloudLauncher.Views;

/// <summary>
/// The Compare tab of the file-management page: how this instance differs from another one for a
/// chosen part of the tree, with a line diff and a way to copy either side over the other.
/// </summary>
/// <remarks>
/// <para>Reuses the Config page's pieces: the verdict is the length-then-hash rule of
/// <see cref="ConfigHubService.CompareGroups"/>, the diff is <see cref="ConfigHubService.Diff"/>,
/// the diff pane is <see cref="ConfigCompareCard"/> in embedded mode, and copies go through
/// <see cref="ConfigHubService.PreviewCopy"/> and <see cref="ConfigHubService.Copy"/>, which keep
/// the <c>.bak-&lt;timestamp&gt;</c> backup and the shared-instance warning.</para>
/// <para>The scope picker exists because comparing two whole modpacks can mean stat-ing 60,000
/// files and hashing every same-size pair. Most questions are about <c>config/</c> or the mod list,
/// so the narrow scopes are the default.</para>
/// </remarks>
public partial class FileComparePanel : UserControl
{
    private readonly MainWindow _shell;
    private readonly PackDetail _pack;
    private readonly PageState _state;

    private readonly ObservableCollection<CompareRow> _rows = new();
    private readonly ObservableCollection<StateChip> _chips = new();
    private readonly Reentrancy _filling = new();

    private List<CompareRow> _all = new();
    private List<PackSummary> _packs = new();

    private PackSummary? _other;
    private ScopeItem _scope = ScopeItem.All[0];
    private CompareState? _stateFilter;
    private string _filter = "";

    private CancellationTokenSource? _workCts;
    private bool _busy;

    /// <summary>The card currently in the diff pane, so it can be unhooked before it is replaced.</summary>
    private ConfigCompareCard? _card;

    private readonly bool _canWriteHere;
    private readonly string _readOnlyHere;

    public FileComparePanel(MainWindow shell, PackDetail pack)
    {
        InitializeComponent();
        _shell = shell;
        _pack = pack;

        _canWriteHere = pack.OwnerId == App.State.Settings.UserId
                        || pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared);
        _readOnlyHere = "You have read-only access to " + pack.Name +
                        ", so the launcher will not write files into it.";

        FileList.ItemsSource = _rows;
        StateStrip.ItemsSource = _chips;

        using (_filling.Hold())
        {
            ScopeBox.ItemsSource = ScopeItem.All;
            ScopeBox.SelectedIndex = 0;
        }

        _state = new PageState(FileList, PageStateHost, nameof(FileComparePanel))
            .Copy(CompareCopy)
            .Slots(SubLabel, StatusLabel, BusyBar, BusyCancelButton)
            .DisableWhileBusy(RefreshButton, OtherBox, ScopeBox);
        _state.RetryRequested += () => _ = LoadAsync();
        _state.CancelRequested += () => _workCts?.Cancel();

        SearchBox.TextChangedDebounced += (_, text) => { _filter = text; ApplyFilter(); };

        Loaded += OnLoaded;
        Unloaded += (_, _) => _workCts?.Cancel();
    }

    private static readonly PageCopy CompareCopy = new()
    {
        Glyph = "",
        Verb = "Comparing",
        Noun = "path(s)",
        LoadingLine = "Reading both instances and comparing them file by file.",
        EmptyTitle = "Nothing to compare",
        EmptyBody = "Neither instance has any file in this part of the tree. Pick a wider scope.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "Every path is still there - the filter or the verdict chip is hiding them.",
        ErrorTitle = "Could not compare these instances",
        OfflineTitle = "Could not list your instances",
        OfflineBody = "The server is not answering ({0}), so the launcher does not know which other "
                    + "instances you have. The files themselves are on this PC and unaffected."
    };

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_state.Kind == PageStateKind.Idle) await LoadAsync();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5) { _ = LoadAsync(); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    // ── which instances ──────────────────────────────────────────────────────

    /// <summary>
    /// A progress sink that always lands on the UI thread.
    /// </summary>
    /// <remarks>
    /// <see cref="Progress{T}"/> captures the current <see cref="SynchronizationContext"/> and falls
    /// back to the thread pool when there is none, as in hosts that do not run the dispatcher (the
    /// off-screen test harness, for one). A <c>CheckAccess</c> per report is cheap and always right.
    /// </remarks>
    /// <param name="format">Turns one reported line into the sentence the status bar shows.</param>
    private IProgress<string> UiProgress(Func<string, string> format)
    {
        var dispatcher = Dispatcher;
        return new Progress<string>(line =>
        {
            if (dispatcher.CheckAccess()) _state.Progress(format(line));
            else dispatcher.BeginInvoke(() => _state.Progress(format(line)));
        });
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        _state.Begin("Listing your instances.");

        try
        {
            _packs = (await App.State.Api.ListPacksAsync())
                .Where(p => !App.State.Settings.IsPackHidden(p.Id))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _busy = false;
            if (Connectivity.DescribeTransportFailure(ex, CancellationToken.None) is { } why)
                _state.Offline(why);
            else
                _state.Error("Your instance list could not be read.", ex);
            return;
        }

        var others = _packs.Where(p => p.Id != _pack.Id).ToList();
        using (_filling.Hold())
        {
            OtherBox.ItemsSource = others.Select(p => new SideItem(p, p.Name)).ToList();
            OtherBox.SelectedIndex = others.Count == 0 ? -1 : 0;
        }

        _busy = false;

        if (others.Count == 0)
        {
            _other = null;
            _all = new List<CompareRow>();
            _rows.Clear();
            _chips.Clear();
            // EmptyNext rather than EmptyCopy: this is only true for this look, and a standing
            // override would keep saying it after a second instance appeared.
            _state.EmptyNext("Only one instance",
                "There is nothing to compare " + _pack.Name + " against yet. Create or download a "
                + "second instance and this tab will line the two of them up.", glyph: "");
            _state.Content(0);
            UpdateCopyButtons();
            return;
        }

        _other = others[0];
        await CompareAsync();
    }

    private void OnOtherChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy) return;
        if (OtherBox.SelectedItem is not SideItem item) return;
        _other = item.Pack;
        _ = CompareAsync();
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy) return;
        if (ScopeBox.SelectedItem is not ScopeItem scope) return;
        _scope = scope;
        _ = CompareAsync();
    }

    // ── the comparison ───────────────────────────────────────────────────────

    private async Task CompareAsync()
    {
        if (_other is not { } other) return;
        if (_busy) return;
        _busy = true;
        _workCts?.Cancel();
        _workCts = new CancellationTokenSource();
        var ct = _workCts.Token;

        ClearDetail();
        _state.Begin($"Comparing {_pack.Name} with {other.Name}.", refreshing: false);

        try
        {
            string hereDir, thereDir;
            try
            {
                hereDir = App.State.Packs.GameDir(_pack.Id);
                thereDir = App.State.Packs.GameDir(other.Id);
            }
            catch (Exception ex)
            {
                _state.Error("One of the two instance folders could not be found.", ex);
                return;
            }

            var scope = _scope;
            var progress = UiProgress(line => line);

            var rows = await Task.Run(() => Compare(hereDir, thereDir, scope, progress, ct), ct);
            if (ct.IsCancellationRequested) return;

            _all = rows;
            RebuildChips();
            // Clear busy before writing the list: ApplyFilter does nothing while busy, so a
            // keystroke mid-scan cannot report "0 paths".
            _busy = false;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            _state.Cancelled("Comparison stopped.");
        }
        catch (Exception ex)
        {
            _state.Error("These two instances could not be compared.", ex);
        }
        finally
        {
            _busy = false;
            UpdateCopyButtons();
        }
    }

    /// <summary>
    /// The verdict for every path in scope. Files of different lengths differ without being read,
    /// as in <see cref="ConfigHubService.CompareGroups"/>. Only same-length pairs are hashed, and
    /// only up to <see cref="ConfigHubService.MaxInspectBytes"/>; hashing larger files (worlds,
    /// jars) would make one click take minutes.
    /// </summary>
    private static List<CompareRow> Compare(string hereDir, string thereDir, ScopeItem scope,
                                            IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Listing both instances...");
        var here = Index(hereDir, scope, ct);
        var there = Index(thereDir, scope, ct);

        var paths = new HashSet<string>(here.Keys, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(there.Keys);

        var rows = new List<CompareRow>(paths.Count);
        var done = 0;

        foreach (var rel in paths)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 500 == 0) progress.Report($"Compared {done:N0} of {paths.Count:N0} path(s)...");

            here.TryGetValue(rel, out var a);
            there.TryGetValue(rel, out var b);

            if (a is null && b is null) continue;      // cannot happen; the set came from the two maps
            if (b is null) { rows.Add(new CompareRow(rel, a, null, CompareState.OnlyHere)); continue; }
            if (a is null) { rows.Add(new CompareRow(rel, null, b, CompareState.OnlyThere)); continue; }

            if (a.Length != b.Length) { rows.Add(new CompareRow(rel, a, b, CompareState.Differs)); continue; }
            if (a.Length > ConfigHubService.MaxInspectBytes)
            {
                rows.Add(new CompareRow(rel, a, b, CompareState.NotRead));
                continue;
            }

            var ha = ConfigHubService.HashOrNull(a.FullPath);
            var hb = ha is null ? null : ConfigHubService.HashOrNull(b.FullPath);
            // An unreadable file is "not read", never "the same": a match is only
            // claimed when verified.
            var state = ha is null || hb is null
                ? CompareState.NotRead
                : ha == hb ? CompareState.Same : CompareState.Differs;
            rows.Add(new CompareRow(rel, a, b, state));
        }

        // Differences first, then the one-sided files, then everything that matched.
        return rows
            .OrderBy(r => r.SortRank)
            .ThenBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, SideFile> Index(string gameDir, ScopeItem scope, CancellationToken ct)
    {
        var map = new Dictionary<string, SideFile>(StringComparer.OrdinalIgnoreCase);
        var root = scope.Prefix.Length == 0
            ? gameDir
            : Path.Combine(gameDir, scope.Prefix.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(root)) return map;

        foreach (var rel in App.State.Packs.ListRelativeFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            FileInfo info;
            try { info = new FileInfo(full); if (!info.Exists) continue; }
            catch { continue; }

            var key = scope.Prefix.Length == 0 ? rel : scope.Prefix + rel;
            map[key] = new SideFile(full, info.Length, info.LastWriteTimeUtc);
        }
        return map;
    }

    // ── chips and filtering ──────────────────────────────────────────────────

    private void RebuildChips()
    {
        _chips.Clear();
        var other = _other?.Name ?? "the other instance";

        _chips.Add(new StateChip(null, "All", _all.Count,
            "Every path either instance has in this scope.") { IsSelected = _stateFilter is null });

        AddChip(CompareState.Differs, "Differs",
            "Both instances have this file and their contents are not the same.");
        AddChip(CompareState.OnlyHere, "Only here",
            $"{_pack.Name} has this file and {other} does not.");
        AddChip(CompareState.OnlyThere, "Only there",
            $"{other} has this file and {_pack.Name} does not.");
        AddChip(CompareState.Same, "Same",
            "Byte-for-byte identical in both instances.");
        AddChip(CompareState.NotRead, "Not read",
            "Same size on both sides, but too large to read or locked by a running game, so the "
            + "launcher will not claim they match.");

        void AddChip(CompareState state, string label, string hint)
        {
            var count = _all.Count(r => r.State == state);
            if (count == 0) return;
            _chips.Add(new StateChip(state, label, count, hint) { IsSelected = _stateFilter == state });
        }
    }

    private void OnStateChipClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StateChip chip) return;
        _stateFilter = chip.State;
        foreach (var c in _chips) c.IsSelected = c.State == chip.State;
        // The chips are plain objects, so the strip is rebound rather than notifying per chip.
        var snapshot = _chips.ToList();
        _chips.Clear();
        foreach (var c in snapshot) _chips.Add(c);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        // Typing during a comparison is fine, but calling Content() mid-scan would turn Loading
        // into "0 paths" before anything had been read.
        if (_busy) return;

        var query = _filter.Trim();
        var shown = _all.Where(r =>
            (_stateFilter is null || r.State == _stateFilter) &&
            (query.Length == 0 || r.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        _rows.Clear();
        foreach (var row in shown) _rows.Add(row);

        // One-shot: Content reverts to the page's own empty wording afterwards.
        if (_all.Count > 0 && shown.Count == 0) _state.EmptyFiltered();

        var differs = _all.Count(r => r.State == CompareState.Differs);
        var note = _other is null
            ? null
            : differs == 0
                ? $"Nothing differs between {_pack.Name} and {_other.Name} in {_scope.Label.ToLowerInvariant()}."
                : $"{differs:N0} file(s) differ between {_pack.Name} and {_other.Name}.";

        _state.Content(shown.Count, note: note,
            countText: shown.Count == _all.Count
                ? $"{_all.Count:N0} path(s)"
                : $"{shown.Count:N0} of {_all.Count:N0} path(s)");

        UpdateCopyButtons();
    }

    // ── the detail pane ──────────────────────────────────────────────────────

    private void OnFileSelected(object sender, SelectionChangedEventArgs e)
    {
        UpdateCopyButtons();
        var selected = FileList.SelectedItems.OfType<CompareRow>().ToList();

        if (selected.Count == 0)
        {
            ShowDetail("", "Pick a file to see what changed",
                "Text files open as a line-by-line diff. Anything else is compared by size and "
                + "contents only.");
            return;
        }
        if (selected.Count > 1)
        {
            var bytes = selected.Sum(r => r.Here?.Length ?? r.There?.Length ?? 0);
            ShowDetail("", $"{selected.Count:N0} files selected",
                $"{ConfigHubService.FormatSize(bytes)} in all. Use the copy buttons below to move the "
                + "whole selection in one direction; pick a single file to see its diff.");
            return;
        }

        ShowFor(selected[0]);
    }

    private void ShowFor(CompareRow row)
    {
        ClearDetail();

        if (_other is not { } other) return;

        if (row.Here is null || row.There is null)
        {
            var side = row.Here is not null ? _pack.Name : other.Name;
            var missing = row.Here is not null ? other.Name : _pack.Name;
            var file = row.Here ?? row.There!;
            ShowDetail("", $"Only in {side}",
                $"{row.RelativePath}\n\n{ConfigHubService.FormatSize(file.Length)}, last written "
                + $"{ConfigHubService.FormatAge(file.ModifiedUtc)}. {missing} has no file at that path, "
                + "so there is nothing to diff against - copy it across to make them match.");
            return;
        }

        if (row.State == CompareState.Same)
        {
            ShowDetail("", "These files are identical",
                $"{row.RelativePath}\n\nBoth instances hold exactly the same "
                + $"{ConfigHubService.FormatSize(row.Here.Length)}.");
            return;
        }

        if (!TextFileService.LooksEditable(row.Here.FullPath))
        {
            ShowDetail("", row.State == CompareState.NotRead
                    ? "Same size, not read"
                    : "Not a text file",
                $"{row.RelativePath}\n\n{_pack.Name}: {ConfigHubService.FormatSize(row.Here.Length)}, "
                + $"{other.Name}: {ConfigHubService.FormatSize(row.There.Length)}. "
                + (row.State == CompareState.NotRead
                    ? "The two copies are the same size but too large to read, so the launcher will "
                      + "not claim they match."
                    : "A jar, image or world file has no line-by-line diff; the sizes above are the "
                      + "whole comparison."));
            return;
        }

        if (row.Here.Length > TextFileService.MaxEditableBytes
            || row.There.Length > TextFileService.MaxEditableBytes)
        {
            ShowDetail("", "Too large to diff",
                $"{row.RelativePath}\n\nOne of the two copies is over "
                + $"{ConfigHubService.FormatSize(TextFileService.MaxEditableBytes)}, which is the "
                + "launcher's ceiling for reading a text file into memory.");
            return;
        }

        // The diff pane is the Config page's compare card, without the floating-card geometry.
        var left = EntryFor(_pack.Id, _pack.Name, row.RelativePath, row.Here);
        var right = EntryFor(other.Id, other.Name, row.RelativePath, row.There);

        var card = new ConfigCompareCard([left, right], left, right, _shell);
        card.Embed();
        card.Copied += OnCardCopied;
        _card = card;

        DetailPanel.Visibility = Visibility.Collapsed;
        DiffHost.Children.Clear();
        DiffHost.Children.Add(card);
    }

    private static Entry EntryFor(Guid packId, string packName, string rel, SideFile file) =>
        new(packId, packName, rel, file.FullPath, file.Length, file.ModifiedUtc,
            ConfigHubService.FileKind.Config);

    private void OnCardCopied() => _ = CompareAsync();

    private void ShowDetail(string glyph, string title, string body)
    {
        // Remove the previous diff first; a card left behind the panel would stay hooked to its events.
        ClearDetail();
        DetailGlyph.Text = glyph;
        DetailTitle.Text = title;
        DetailBody.Text = body;
        DetailPanel.Visibility = Visibility.Visible;
    }

    private void ClearDetail()
    {
        if (_card is not null) { _card.Copied -= OnCardCopied; _card = null; }
        DiffHost.Children.Clear();
        DetailPanel.Visibility = Visibility.Visible;
    }

    // ── copying ──────────────────────────────────────────────────────────────

    private void UpdateCopyButtons()
    {
        var selected = FileList.SelectedItems.OfType<CompareRow>().ToList();
        var other = _other;

        var canWriteThere = other is not null
            && (other.OwnerId == App.State.Settings.UserId
                || other.EffectivePermissions.HasFlag(PackPermissions.UploadShared));

        var toCount = selected.Count(r => r.Here is not null);
        var fromCount = selected.Count(r => r.There is not null);

        CopyToButton.Content = other is null
            ? "Copy to..."
            : toCount switch
            {
                0 => $"Copy to {other.Name}",
                1 => $"Copy 1 file to {other.Name}",
                _ => $"Copy {toCount:N0} files to {other.Name}"
            };
        CopyFromButton.Content = other is null
            ? "Copy from..."
            : fromCount switch
            {
                0 => $"Copy from {other.Name}",
                1 => $"Copy 1 file from {other.Name}",
                _ => $"Copy {fromCount:N0} files from {other.Name}"
            };

        CopyToButton.IsEnabled = !_busy && toCount > 0 && canWriteThere;
        CopyFromButton.IsEnabled = !_busy && fromCount > 0 && _canWriteHere;

        CopyToButton.ToolTip = other is null
            ? "There is no other instance to copy into."
            : canWriteThere
                ? $"Replace {other.Name}'s copy of the selected files with this instance's. Anything "
                  + "replaced is backed up first."
                : $"You have read-only access to {other.Name}, so the launcher will not write files "
                  + "into it.";
        CopyFromButton.ToolTip = other is null
            ? "There is no other instance to copy from."
            : _canWriteHere
                ? $"Replace this instance's copy of the selected files with {other.Name}'s. Anything "
                  + "replaced is backed up first."
                : _readOnlyHere;

        ToolTipService.SetShowOnDisabled(CopyToButton, true);
        ToolTipService.SetShowOnDisabled(CopyFromButton, true);
    }

    private void OnCopyToOther(object sender, RoutedEventArgs e)
    {
        if (_other is not { } other) return;
        var paths = FileList.SelectedItems.OfType<CompareRow>()
                            .Where(r => r.Here is not null)
                            .Select(r => r.RelativePath).ToList();
        _ = CopyAsync(_pack.Name, App.State.Packs.GameDir(_pack.Id), other, paths);
    }

    private void OnCopyFromOther(object sender, RoutedEventArgs e)
    {
        if (_other is not { } other) return;
        if (ThisAsSummary() is not { } me) return;
        var paths = FileList.SelectedItems.OfType<CompareRow>()
                            .Where(r => r.There is not null)
                            .Select(r => r.RelativePath).ToList();
        _ = CopyAsync(other.Name, App.State.Packs.GameDir(other.Id), me, paths);
    }

    /// <summary>This instance as a <see cref="PackSummary"/>, the copy target type. Taken from the
    /// fetched list rather than built here, so its permissions are the server's.</summary>
    private PackSummary? ThisAsSummary() => _packs.FirstOrDefault(p => p.Id == _pack.Id);

    /// <summary>
    /// Runs one direction of a copy through the Config page's card and service, so the
    /// destination's rules decide the sharing warning and every replaced file is backed up.
    /// </summary>
    private async Task CopyAsync(string sourceName, string sourceGameDir, PackSummary target,
                                 List<string> paths)
    {
        if (paths.Count == 0 || _busy) return;

        try
        {
            // The picker takes the pane rather than a modal backdrop, since this page already
            // fills the window.
            var card = new ConfigCopyCard(sourceName, paths, [target], _shell);
            card.Embed();

            var restore = _card;
            DetailPanel.Visibility = Visibility.Collapsed;
            DiffHost.Children.Clear();
            DiffHost.Children.Add(card);

            // Hide the status bar's copy buttons while the picker, with its own accent button, is up.
            CopyToButton.Visibility = Visibility.Collapsed;
            CopyFromButton.Visibility = Visibility.Collapsed;

            await card.Completion;

            CopyToButton.Visibility = Visibility.Visible;
            CopyFromButton.Visibility = Visibility.Visible;

            DiffHost.Children.Clear();
            if (restore is not null) DiffHost.Children.Add(restore);
            else DetailPanel.Visibility = Visibility.Visible;

            if (card.ChosenTargets is not { Count: > 0 }) return;

            var preview = card.Preview;
            var overwriting = preview?.Items.Count(i => i.Exists) ?? 0;
            var shared = preview?.SharedPacks ?? [];

            var message =
                $"Copy {paths.Count:N0} file(s) from {sourceName} into {target.Name}.\n\n" +
                (overwriting > 0
                    ? $"{overwriting:N0} existing file(s) will be replaced. Each one is backed up next "
                      + "to itself as .bak-<timestamp> first.\n\n"
                    : "Nothing existing will be replaced.\n\n") +
                (shared.Count > 0
                    ? $"Careful: {target.Name} is shared and its rules mark these paths as Shared, so "
                      + "the change will travel to everyone else on that pack the next time they "
                      + "sync.\n\n"
                    : "") +
                "Continue?";

            if (!await AppDialog.ConfirmAsync(_shell, "Copy between instances", message,
                    "Copy", "Cancel", danger: shared.Count > 0))
                return;

            _busy = true;
            _workCts?.Cancel();
            _workCts = new CancellationTokenSource();
            var ct = _workCts.Token;
            _state.Begin("Copying files between the two instances.", refreshing: true);

            var progress = UiProgress(what => "Copying " + what);
            var result = await Task.Run(() => ConfigHubService.Copy(
                sourceGameDir, paths, [target], App.State.Packs, progress, ct), ct);

            AppLog.Log("files", $"Copied {result.Copied} file(s) from {sourceName} into {target.Name}"
                                + (result.BackedUp > 0 ? $"; {result.BackedUp} replaced file(s) backed up" : "")
                                + (result.Failures.Count > 0 ? $"; {result.Failures.Count} failed" : "") + ".");
            foreach (var line in result.Failures.Take(20)) AppLog.Log("files", "  " + line);

            _busy = false;
            await CompareAsync();

            _state.Note($"Copied {result.Copied:N0} file(s) into {target.Name}"
                        + (result.BackedUp > 0 ? $" · {result.BackedUp:N0} replaced file(s) backed up" : "")
                        + (result.Failures.Count > 0
                            ? $" · {result.Failures.Count:N0} could not be written; the details are in "
                              + "the launcher log."
                            : "") + ".");
        }
        catch (OperationCanceledException)
        {
            _state.Cancelled("Copy stopped. Files already written were left in place and backed up.");
        }
        catch (Exception ex)
        {
            _state.Error("Those files could not be copied.", ex);
        }
        finally
        {
            _busy = false;
            CopyToButton.Visibility = Visibility.Visible;
            CopyFromButton.Visibility = Visibility.Visible;
            UpdateCopyButtons();
        }
    }

    // ── rows ─────────────────────────────────────────────────────────────────

    private enum CompareState { Differs, OnlyHere, OnlyThere, NotRead, Same }

    /// <summary>One side's copy of a path.</summary>
    private sealed record SideFile(string FullPath, long Length, DateTime ModifiedUtc);

    private sealed class CompareRow(string rel, SideFile? here, SideFile? there, CompareState state)
    {
        public string RelativePath { get; } = rel;
        public SideFile? Here { get; } = here;
        public SideFile? There { get; } = there;
        public CompareState State { get; } = state;

        public string FileName { get; } = Path.GetFileName(rel);

        public string FolderLabel { get; } = rel.LastIndexOf('/') is var slash && slash > 0
            ? rel[..slash] + "/"
            : "";

        /// <summary>The string the XAML's DataTriggers switch on, so the badge needs no converter.</summary>
        public string StateName { get; } = state.ToString();

        public string StateLabel { get; } = state switch
        {
            CompareState.Differs => "DIFFERS",
            CompareState.OnlyHere => "ONLY HERE",
            CompareState.OnlyThere => "ONLY THERE",
            CompareState.NotRead => "NOT READ",
            _ => "SAME"
        };

        public string SizeLabel { get; } =
            here is not null && there is not null
                ? here.Length == there.Length
                    ? ConfigHubService.FormatSize(here.Length) + " on both sides"
                    : ConfigHubService.FormatSize(here.Length) + " here · "
                      + ConfigHubService.FormatSize(there.Length) + " there"
                : ConfigHubService.FormatSize((here ?? there)!.Length);

        /// <summary>Sort order: differences first, matches last.</summary>
        public int SortRank { get; } = (int)state;
    }

    private sealed class SideItem(PackSummary pack, string label)
    {
        public PackSummary Pack { get; } = pack;
        public string Label { get; } = label;
    }

    /// <summary>One verdict chip. Selection is mutable and the strip is rebound as a whole, which
    /// is simpler than INotifyPropertyChanged for five chips.</summary>
    private sealed class StateChip(CompareState? state, string label, int count, string hint)
    {
        public CompareState? State { get; } = state;
        public string Label { get; } = label;
        public string CountLabel { get; } = count.ToString("N0");
        public string Hint { get; } = hint;
        public bool IsSelected { get; set; }
    }

    /// <summary>Which part of the tree a comparison covers.</summary>
    private sealed class ScopeItem(string label, string prefix)
    {
        public string Label { get; } = label;

        /// <summary>Game-relative folder, with a trailing slash, or "" for the whole instance.</summary>
        public string Prefix { get; } = prefix;

        public static readonly List<ScopeItem> All =
        [
            new("Config", "config/"),
            new("KubeJS scripts", "kubejs/"),
            new("Mods", "mods/"),
            new("Resource packs", "resourcepacks/"),
            new("The whole instance", "")
        ];
    }
}
