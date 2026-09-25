using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>Every resource pack on this PC, in a list, grouped into folders you make.</summary>
/// <remarks>
/// <para>Each row has one switch, the pack's "on by default" bit: on means every compatible
/// instance gets it, and the pack's own page (<see cref="LocalResourcePackDetailView"/>) narrows
/// that down. Folders are named lists in <see cref="AppSettings.ResourcePackFolders"/>, as on the
/// Instances page; nothing moves on disk when a pack is filed.</para>
/// <para>Load order is per instance (options.txt), so it is edited on that instance's Resources
/// tab. #1 is the pack that wins.</para>
/// <para>Rows are painted from <see cref="ScanCache{T}"/> first, because reading a pack's name and
/// icon means opening its zip (see <see cref="PageRefresh"/>). The page is reused
/// (<see cref="IReusablePage"/>): subscriptions are made in Loaded and dropped in Unloaded.</para>
/// </remarks>
public partial class ResourcePacksView : Page, IReusablePage, IRefreshablePage
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<RpFolderChipVm> _folderChips = new();

    /// <summary>Everything the scan found; <see cref="ApplyFilter"/> narrows it for display.</summary>
    private readonly ObservableCollection<ContentRow> _all = new();

    /// <summary>Stops the scope ComboBox reacting to its own fill: setting ItemsSource and then
    /// SelectedItem raises SelectionChanged synchronously.</summary>
    private readonly Reentrancy _filling = new();

    private readonly PageState _state;
    private CancellationTokenSource? _scanCts;

    private List<PackSummary> _packs = new();
    private RpModel _model = new();
    private ResourcePackSortMode _sortMode;
    private string? _activeFolder;
    private Guid? _scopePackId;

    /// <summary>Why the instance list may be old (offline). The next field holds the
    /// note <see cref="PageRefresh"/> writes while a remembered scan is shown; the
    /// filter re-applies both.</summary>
    private string? _staleNote;
    private string? _cacheNote;

    /// <summary>What <see cref="ResourcePackConflicts"/> found in the scoped instance, or null. Kept
    /// because computing it reads every pack's zip.</summary>
    private string? _conflictNote;

    /// <summary>Id of the trailing "New folder" chip. <see cref="IsReservedFolderName"/> refuses
    /// folder names starting with a colon, so it can't collide with a user folder.</summary>
    private const string NewFolderChipId = ":new";

    /// <summary>The plus on the "New folder" chip. The shared chip style draws that chip outlined
    /// (FolderChipBorder in Themes/Controls.xaml).</summary>
    private const string NewFolderGlyph = "";

    /// <summary>A remembered row, kept across restarts. The icon is left out: a few
    /// hundred base64 PNGs would blow the cache's size budget, and the in-session meta
    /// cache makes icons cheap anyway.</summary>
    /// <remarks>Changing this shape discards every existing cached scan.</remarks>
    public sealed record CachedRpRow(
        string FileName,
        string FilePath,
        string DisplayName,
        DateTimeOffset Modified,
        long SizeBytes,
        bool CompatibleWithAll,
        string CompatibleCsv,
        bool IsLocal,
        bool IsFolder,
        int StackIndex,
        string Description,
        int PackFormat);

    private static readonly ScanCache<CachedRpRow> Cache =
        ScanCaches.For<CachedRpRow>(ScanKinds.ResourcePacks);

    public ResourcePacksView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        // Manual (the old drag order) has no menu item any more, but older settings files can still
        // name it. Map it to a mode the sort menu can show.
        _sortMode = App.State.Settings.ResourcePackSortMode == ResourcePackSortMode.Manual
            ? ResourcePackSortMode.Modified
            : App.State.Settings.ResourcePackSortMode;
        FolderStrip.ItemsSource = _folderChips;

        PackList.Configure(new ContentListHost
        {
            Open = OpenRow,
            Toggled = ToggleRowAsync,
            // A switch, since any number of packs can be on at once. The Shaders page uses Choice.
            RowControl = ContentRowControl.Toggle,
            Menu = BuildRowMenu,
            Status = Okay
        });

        _state = new PageState(PackList, PageStateHost, nameof(ResourcePacksView))
            .Copy(PageCopy.ResourcePacks)
            .Slots(CountLabel, StatusLabel, BusyBar, BusyCancelButton)
            .DisableWhileBusy(RefreshButton, SortButton, PackScopeBox);
        _state.EmptyCopy(
            "No resource packs yet",
            "Import a pack you already have, or find one in the store. Anything an instance already "
            + "has shows up here too, and one switch is all it takes to have it handed to every "
            + "compatible instance.",
            "Import a pack",
            () => OnImport(this, new RoutedEventArgs()));
        _state.RetryRequested += () => _ = LoadAsync(force: true);
        _state.CancelRequested += () =>
        {
            _scanCts?.Cancel();
            _state.Cancelled("Scan stopped.");
        };

        SearchBox.TextChangedDebounced += (_, _) => ApplyFilter();

        // Subscribed on Loaded and removed on Unloaded. The page is reused (IReusablePage), so
        // subscribing in the constructor would leave these dead after the first navigation.
        Loaded += async (_, _) =>
        {
            Window.GetWindow(this)!.PreviewKeyDown += GlobalKeys;
            App.State.ConnectivityChanged += OnConnectivityChanged;
            await LoadAsync();
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window w) w.PreviewKeyDown -= GlobalKeys;
            App.State.ConnectivityChanged -= OnConnectivityChanged;
            _scanCts?.Cancel();
        };
    }

    // ── loading ──────────────────────────────────────────────────────────────

    /// <summary>What one pass of the scan found. Assigned once, at the end of the worker.</summary>
    private sealed class RpModel
    {
        public List<LibraryItem> Library { get; init; } = [];
        public List<RpInstalled> Installed { get; init; } = [];
        public Dictionary<Guid, InstanceContentOverrides> Overrides { get; init; } = new();

        /// <summary>Row key -> the numbers behind that row, so the filter and sort never parse row
        /// text.</summary>
        public Dictionary<string, RpRowFacts> Facts { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset? ScannedUtc { get; init; }
        public int Failed { get; init; }
    }

    /// <summary>The counts behind one row.</summary>
    /// <param name="Present">Instances holding this item's bytes (1 for
    /// an instance's own file).</param>
    /// <param name="On">Of those, the ones whose options.txt lists it.</param>
    /// <param name="Reach">Instances this pack's switch currently puts it
    /// in. Zero when it is off.</param>
    /// <param name="Format">The pack's own <c>pack_format</c>. Null when the file does not say.</param>
    private sealed record RpRowFacts(
        int Present, int On, int Reach, long SizeBytes, DateTimeOffset Modified, int? Format = null);

    /// <summary>One pack found inside one instance.</summary>
    private sealed record RpInstalled(PackSummary Pack, ResourcePackInfo Info, ResourcePackMeta Meta);

    /// <inheritdoc cref="IRefreshablePage.RefreshAsync"/>
    /// <remarks>Re-reads the instance list and re-runs the scan, keeping the rows, scroll position and
    /// selection.</remarks>
    public Task RefreshAsync() => LoadAsync();

    /// <summary>Paints the remembered scan, then re-reads the library and every instance.</summary>
    /// <param name="force">Ignore the cache and re-walk the folders (Refresh, F5, and after this page
    /// writes a file).</param>
    private async Task LoadAsync(bool force = false)
    {
        // New token source every pass, so a reused page doesn't come back with a cancelled token.
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        var ok = await PageRefresh.RunAsync(
            _state, _all, PackList.ListControl, r => r.Key,
            cached: () => force ? CachedRows<ContentRow>.None : Remembered(),
            rescan: token => Rescan(force, token),
            ct: ct,
            filter: () => ApplyFilter(announce: true),
            note: n => _cacheNote = n,
            update: (row, scanned) => row.CopyFrom(scanned),
            prepare: PrepareAsync,
            failed: "Your resource packs could not be read.",
            cachedNote: age => age is { Length: > 0 }
                ? $"Showing the last scan ({age}) - re-reading every instance now."
                : "Showing the last scan - re-reading every instance now.",
            quiet: !force);

        if (!ok) return;

        if (_model.Failed > 0)
            _state.Note($"{_model.Failed} instance(s) could not be read - see the launcher log.");
        else if (_staleNote is { Length: > 0 } why)
            _state.Note(why);

        // Icons last: the list is already usable and this is the part that opens files.
        await LoadIconsAsync(ct);
    }

    /// <summary>Cheap setup for the remembered paint: the cache file, the instance list and the scope
    /// box.</summary>
    private async Task PrepareAsync(CancellationToken ct)
    {
        await Cache.EnsureLoadedAsync();

        // ListPacksAsync already falls back to the cached list and a disk scan, so this rarely fails.
        // If it does, keep scanning against the last list we had.
        _staleNote = null;
        try
        {
            // Reuse the Instances page's recent list, if any, to skip a round trip.
            _packs = await App.State.Api.ListPacksQuickAsync(ct);
            _staleNote = App.State.Api.PackListStale is { Length: > 0 } why
                ? $"Showing packs for your last known instances - {why}."
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (OfflineException ox)
        {
            _staleNote = ox.Reason is { Length: > 0 } reason
                ? $"Showing packs for your last known instances - {reason}."
                : "Showing packs for your last known instances.";
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ResourcePacksView), ex);
            _staleNote = "The instance list could not be refreshed; scanning the instances already known.";
        }

        RebuildScopeBox();
    }

    /// <summary>The remembered list for the first frame, built without opening any pack.</summary>
    /// <remarks>Library items are matched to instance copies by file name only, since
    /// checking for the same file needs a handle per candidate. The real scan corrects any
    /// mismatch a moment later.</remarks>
    private CachedRows<ContentRow> Remembered()
    {
        try
        {
            var installed = new List<RpInstalled>();
            DateTimeOffset? oldest = null;

            foreach (var pack in _packs)
            {
                var hit = Cache.Get(ScanCache<CachedRpRow>.ScopeKey(pack.Id));
                if (hit.State != ScanState.Cached) continue;
                if (hit.ScannedUtc is { } at && (oldest is null || at < oldest)) oldest = at;
                foreach (var row in hit.Rows)
                    if (IsInstancePackPath(pack, row)) installed.Add(Materialise(pack, row));
            }

            var model = new RpModel
            {
                Library = SafeLibraryScan(),
                Installed = installed,
                Overrides = LoadOverrides(_packs),
                ScannedUtc = oldest
            };
            if (model.Library.Count == 0 && installed.Count == 0) return CachedRows<ContentRow>.None;

            _model = model;
            return new CachedRows<ContentRow>(BuildRows(model, cheap: true), oldest);
        }
        catch (Exception ex)
        {
            // Nothing remembered is fine; the real scan follows.
            AppLog.LogError(nameof(ResourcePacksView), ex);
            return CachedRows<ContentRow>.None;
        }
    }

    /// <summary>The real scan: the library, then every instance whose folders changed.</summary>
    /// <remarks>Runs on a worker thread. Rows are plain view models and icons are frozen bitmaps (see
    /// <c>ResourcePackService.LoadIcon</c>), so they are safe to hand to the UI thread.</remarks>
    private IReadOnlyList<ContentRow> Rescan(bool force, CancellationToken ct)
    {
        var library = SafeLibraryScan();
        var installed = new List<RpInstalled>();
        DateTimeOffset? oldest = null;
        var failed = 0;

        foreach (var pack in _packs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var scope = ScanCache<CachedRpRow>.ScopeKey(pack.Id);
                var fingerprint = FingerprintFor(pack, ct);
                var hit = Cache.Get(scope);

                if (!force && hit.State == ScanState.Cached && hit.Fingerprint == fingerprint
                    && hit.Rows.All(r => IsInstancePackPath(pack, r)))
                {
                    if (hit.ScannedUtc is { } at && (oldest is null || at < oldest)) oldest = at;
                    foreach (var row in hit.Rows) installed.Add(Materialise(pack, row));
                    continue;
                }

                var fresh = new List<CachedRpRow>();
                foreach (var info in ScanOneInstance(pack))
                {
                    var meta = ResourcePackService.ReadMeta(info.FilePath, info.IsFolder);
                    fresh.Add(ToCached(info, meta));
                    installed.Add(new RpInstalled(pack, info, meta));
                }
                Cache.Put(scope, fresh, fingerprint);
                oldest ??= DateTimeOffset.Now;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                AppLog.LogError(nameof(ResourcePacksView), ex);
            }
        }

        var model = new RpModel
        {
            Library = library,
            Installed = installed,
            Overrides = LoadOverrides(_packs),
            ScannedUtc = oldest,
            Failed = failed
        };

        // Check again before publishing, so a superseded pass can't leave its model behind for the
        // newer pass's rows to read counts from.
        ct.ThrowIfCancellationRequested();
        _model = model;
        _conflictNote = ConflictNote(ct);
        return BuildRows(model, cheap: false);
    }

    /// <summary>One instance's packs, including the <c>local/</c> folder, minus the copies the launch
    /// overlay links into game/.</summary>
    /// <remarks><c>ScanPack(PackSummary)</c> only includes local/ for shared instances, which
    /// would hide library items on private ones until the first launch. After a launch the same
    /// local/ file also appears in game/. An instance's own same-named pack is a different file
    /// and keeps its own row.</remarks>
    private static List<ResourcePackInfo> ScanOneInstance(PackSummary pack)
    {
        var all = App.State.ResourcePacks.ScanPack(pack.Id, pack.Name, includeLocal: true);
        var game = all.Where(r => !r.IsLocal).ToList();

        return all.Where(r =>
            {
                if (!r.IsLocal) return true;
                var twin = game.FirstOrDefault(g =>
                    string.Equals(g.FileName, r.FileName, StringComparison.OrdinalIgnoreCase));
                return twin is null || !PackFolderService.EntriesReferToSameContent(r.FilePath, twin.FilePath);
            })
            .ToList();
    }

    /// <summary>Fingerprint of both resourcepacks folders and options.txt.</summary>
    /// <remarks>Catches creates, deletes and resizes at any depth, and edits to options.txt. A
    /// same-size edit inside a pack is missed; Refresh covers that.</remarks>
    private static string FingerprintFor(PackSummary pack, CancellationToken ct)
    {
        var gameDir = App.State.Packs.GameDir(pack.Id, pack.Name);
        return new Fingerprint()
            .Add("game", RootFingerprint.Compute(Path.Combine(gameDir, "resourcepacks"), null, 3, ct))
            .Add("local", RootFingerprint.Compute(
                Path.Combine(App.State.Packs.LocalDir(pack.Id), "resourcepacks"), null, 3, ct))
            .Add("options", RootFingerprint.File(Path.Combine(gameDir, "options.txt")))
            .ToString();
    }

    private static CachedRpRow ToCached(ResourcePackInfo info, ResourcePackMeta meta) => new(
        FileName: info.FileName,
        FilePath: info.FilePath,
        DisplayName: info.DisplayName,
        Modified: info.LastModified,
        SizeBytes: info.SizeBytes,
        CompatibleWithAll: info.CompatibleWithAll,
        CompatibleCsv: string.Join(',', info.CompatiblePackIds.Select(g => g.ToString("N"))),
        IsLocal: info.IsLocal,
        IsFolder: info.IsFolder,
        StackIndex: info.StackIndex,
        Description: meta.Description ?? "",
        PackFormat: meta.PackFormat ?? -1);

    /// <summary>True when a remembered row points directly into the instance's resourcepacks folder.
    /// Delete and rename act on the remembered path, so other rows are dropped and the folder is
    /// re-read.</summary>
    private static bool IsInstancePackPath(PackSummary pack, CachedRpRow r)
    {
        string dir;
        try
        {
            // The overloads without a name, which never create a missing instance folder.
            dir = Path.Combine(r.IsLocal ? App.State.Packs.LocalDir(pack.Id) : App.State.Packs.GameDir(pack.Id),
                               "resourcepacks");
        }
        catch { return false; } // the instance has no folder on this PC any more

        try
        {
            if (PathSafety.ResolveFileName(dir, r.FileName) is { } expected
                && string.Equals(expected, Path.GetFullPath(r.FilePath), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { /* a path that cannot be read is treated like a wrong one */ }
        AppLog.Log(nameof(ResourcePacksView), $"Ignored a remembered resource pack outside its folder: {r.FilePath}");
        return false;
    }

    /// <summary>A remembered row, back as the pair the page works in. Opens nothing.</summary>
    private static RpInstalled Materialise(PackSummary pack, CachedRpRow r)
    {
        var origin = r.IsLocal ? ResourcePackOrigin.Local : ResourcePackOrigin.Game;
        var compat = r.CompatibleCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();
        if (!compat.Contains(pack.Id)) compat.Insert(0, pack.Id);

        var info = new ResourcePackInfo(
            Key: ResourcePackService.Key(pack.Id, r.FileName, origin),
            SourcePackId: pack.Id,
            SourcePackName: pack.Name,
            FileName: r.FileName,
            FilePath: r.FilePath,
            DisplayName: r.DisplayName,
            LastModified: r.Modified,
            SizeBytes: r.SizeBytes,
            CompatibleWithAll: r.CompatibleWithAll,
            CompatiblePackIds: compat,
            Origin: origin,
            IsFolder: r.IsFolder,
            StackIndex: r.StackIndex);

        var meta = new ResourcePackMeta(null,
            string.IsNullOrWhiteSpace(r.Description) ? null : r.Description,
            r.PackFormat < 0 ? null : r.PackFormat);

        return new RpInstalled(pack, info, meta);
    }

    private static List<LibraryItem> SafeLibraryScan()
    {
        try { return App.State.Library.Scan(LibraryKind.ResourcePack); }
        catch (Exception ex) { AppLog.LogError(nameof(ResourcePacksView), ex); return []; }
    }

    /// <summary>Each instance's overrides of the defaults: one small JSON read per instance, and a
    /// missing file means nothing was decided.</summary>
    private static Dictionary<Guid, InstanceContentOverrides> LoadOverrides(IReadOnlyList<PackSummary> packs)
    {
        var map = new Dictionary<Guid, InstanceContentOverrides>();
        foreach (var pack in packs)
        {
            try { map[pack.Id] = App.State.ContentDefaults.OverridesFor(pack.Id); }
            catch (Exception ex) { AppLog.LogError(nameof(ResourcePacksView), ex); }
        }
        return map;
    }

    /// <summary>Reads <c>pack.png</c> for rows that have no icon yet.</summary>
    /// <remarks>Usually only instance rows; the scan already read the library's own files.</remarks>
    private async Task LoadIconsAsync(CancellationToken ct)
    {
        var pending = _all
            .Where(r => r.Icon is null && r.Path is { Length: > 0 })
            .Select(r => (Row: r, r.Path))
            .ToList();
        if (pending.Count == 0) return;

        foreach (var chunk in pending.Chunk(24))
        {
            if (ct.IsCancellationRequested) return;
            var work = chunk.ToList();
            var read = await Task.Run(
                () => work.Select(w => (w.Row, Meta: ResourcePackService.ReadMeta(
                    w.Path!, Directory.Exists(w.Path!)))).ToList(), ct);
            if (ct.IsCancellationRequested) return;
            foreach (var (row, meta) in read) row.SetIcon(meta.Icon);
        }
    }

    private void OnConnectivityChanged()
    {
        // Only the instance list comes from the server, so offline is just a note; the packs are local.
        if (App.State.IsOffline && _state.Kind is not (PageStateKind.Loading or PageStateKind.Refreshing))
            _state.Note("The server is not answering. The packs themselves are on this PC.");
    }

    // ── which packs override which, in the scoped instance ───────────────────

    /// <summary>One line naming the packs that override each other in the scoped instance.</summary>
    /// <remarks>Runs on the scan's worker thread because it opens every enabled pack's zip (central
    /// directory only, and <see cref="ResourcePackConflicts"/> caps packs and entries). Null with no
    /// instance in scope: overriding only makes sense within one instance's stack.</remarks>
    private string? ConflictNote(CancellationToken ct)
    {
        if (_scopePackId is not Guid id) return null;
        var scope = _packs.FirstOrDefault(p => p.Id == id);
        if (scope is null) return null;

        try
        {
            var stack = App.State.ResourcePacks.ActiveFor(scope.Id, scope.Name);
            var files = new List<(string FileName, string Path)>();
            foreach (var name in stack)
            {
                var hit = _model.Installed.FirstOrDefault(i =>
                    i.Pack.Id == scope.Id
                    && string.Equals(i.Info.FileName, name, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) files.Add((hit.Info.FileName, hit.Info.FilePath));
            }
            return ResourcePackConflicts.Describe(files, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppLog.LogError("resourcepacks.conflicts", ex); return null; }
    }

    // ── building the rows ────────────────────────────────────────────────────

    /// <summary>One row per library item, plus the scoped instance's own packs.</summary>
    /// <param name="cheap">True on the remembered paint: match library items to instance copies by file
    /// name only. See <see cref="Remembered"/>.</param>
    /// <remarks>A library item and its copies inside instances share one row. An instance's own
    /// same-named file is a different file, so the library row gets an "OWN COPY" pill to show
    /// which one the game loads.</remarks>
    private List<ContentRow> BuildRows(RpModel model, bool cheap)
    {
        var rows = new List<ContentRow>();
        var folded = new HashSet<string>(StringComparer.Ordinal);
        var scope = ScopedPack();

        foreach (var item in model.Library)
        {
            var present = new List<RpInstalled>();
            var shadowing = new List<RpInstalled>();
            foreach (var installed in model.Installed)
            {
                if (!string.Equals(installed.Info.FileName, item.FileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (SameFile(item.Path, installed.Info.FilePath, cheap))
                {
                    present.Add(installed);
                    folded.Add(installed.Info.Key);
                }
                else shadowing.Add(installed);
            }
            rows.Add(new ContentRow(LibraryState(item, present, shadowing, model, scope, cheap)));
        }

        // Every instance's own non-library packs, one row per instance and file. Same-named packs in
        // two instances are two files, so they aren't merged. A scope narrows this to one instance.
        foreach (var installed in model.Installed)
        {
            if (scope is not null && installed.Pack.Id != scope.Id) continue;
            if (folded.Contains(installed.Info.Key)) continue;
            rows.Add(new ContentRow(InstanceState(installed, model, scope)));
        }

        return rows;
    }

    /// <summary>Are these two paths the same bytes? Name-only while painting from memory.</summary>
    private static bool SameFile(string libraryPath, string instancePath, bool cheap)
    {
        if (cheap) return true;   // the caller has already matched the file name
        try { return PackFolderService.EntriesReferToSameContent(libraryPath, instancePath); }
        catch (Exception ex) { AppLog.LogError(nameof(ResourcePacksView), ex); return false; }
    }

    private ContentRowState LibraryState(
        LibraryItem item, List<RpInstalled> present, List<RpInstalled> shadowing,
        RpModel model, PackSummary? scope, bool cheap)
    {
        var meta = ResourcePackMetaFor(item, present, cheap);
        var format = meta.PackFormat;
        var on = present.Count(p => p.Info.Enabled);
        var isDefault = item.AutoApply;

        // Use ContentPlacement so the count matches what the engine will actually place.
        var reach = ContentPlacement.Reach(item, _packs, model.Overrides);

        var here = scope is null ? null : present.FirstOrDefault(p => p.Pack.Id == scope.Id);
        var excludedHere = scope is not null
                           && !ContentPlacement.GoesInto(item, scope, model.Overrides.GetValueOrDefault(scope.Id))
                           && isDefault;

        var advisory = scope is not null
            ? ResourcePackFormats.Advisory(format, scope.MinecraftVersion)
            : null;

        model.Facts[item.Key] = new RpRowFacts(present.Count, on, reach, item.SizeBytes,
                                               item.LastModified, format);

        var folder = FolderOf(item.Key);

        var tooltip = new List<string> { item.DisplayName, item.FileName };
        tooltip.Add(isDefault
            ? $"On: every compatible instance gets it - {reach} of your {_packs.Count} right now. "
              + "Open its page to narrow that."
            : "Off: nothing places it anywhere until you switch it on or put it into an instance "
              + "yourself.");
        tooltip.Add(present.Count == 0
            // Nothing is placed until an instance's next launch, so for an on pack "nowhere" means "not
            // yet".
            ? isDefault
                ? "No instance holds a copy yet - each one takes its own the next time it starts."
                : "Not in any instance yet."
            : $"In {present.Count} instance(s), switched on in {on}: "
              + string.Join(", ", present.Select(p => p.Info.Enabled
                  ? $"{p.Pack.Name} (on, #{p.Info.Priority})"
                  : $"{p.Pack.Name} (off)"))
              // Positions aren't comparable across instances, but what #1 means is the same everywhere.
              + (on > 0 ? ". #1 is the pack that wins in that instance." : ""));
        if (shadowing.Count > 0)
            tooltip.Add($"{string.Join(", ", shadowing.Select(s => s.Pack.Name))} "
                      + "has its own file of that name, which wins at launch.");
        if (format is int f) tooltip.Add(ResourcePackFormats.Describe(f));
        if (advisory is { Length: > 0 }) tooltip.Add(advisory);
        if (meta.Description is { Length: > 0 } description) tooltip.Add(description.ReplaceLineEndings(" "));

        return new ContentRowState
        {
            Key = item.Key,
            DisplayName = item.DisplayName,
            Item = item,
            Path = item.Path,
            MetaLine = $"{item.FileName}  ·  {item.SizeLabel}  ·  added {TimeFormat.Date(item.AddedAt)}",
            // No RulesLine: a pack is simply on or off. An on pack with no copies yet is normal
            // (nothing is placed until each instance's next launch), so "due in N at launch" is kept
            // separate from "on in X of Y", which counts real copies.
            CoverageLine =
                present.Count > 0  ? $"on in {on} of {present.Count}"
                : !isDefault       ? "in no instance"
                : _packs.Count == 0 ? "no instances yet"
                : reach == 0       ? "no instance ticked"
                :                    $"due in {reach} at launch",
            CoverageTip = (present.Count == 0
                              ? "No instance holds a copy of this yet. "
                              : $"In {present.Count} of your {_packs.Count} instance(s), switched on in {on}. ")
                        + (isDefault
                            ? $"It is on, so {reach} instance(s) are due a copy - each one takes its "
                              + "own the next time it starts, or use \"Place everything that is on\" "
                              + "on this row's menu to do it now. Open its page to untick any of them."
                            : "It is off, so nothing places it for you."),
            Tooltip = string.Join("\n", tooltip),
            IsDefault = isDefault,
            HasOwnCopy = shadowing.Count > 0,
            OwnCopyTip = shadowing.Count == 0
                ? ""
                : $"{string.Join(", ", shadowing.Select(s => s.Pack.Name))} has its own copy of "
                  + $"{item.FileName}; the shared one is not in use there. The launch overlay never "
                  + "overwrites, so that copy is the one the game loads.",
            FolderLabel = folder ?? "",
            FolderTip = folder is null ? "" : $"Filed under {folder}",
            IsLocalOnly = item.KeepLocal,
            LocalTip = "Kept on this machine: never uploaded when a shared instance syncs, so nobody "
                     + "else on that instance gets it.",
            OffHere = scope is not null && (excludedHere || (here is not null && !here.Info.Enabled)),
            OffHereTip = scope is null
                ? ""
                : excludedHere
                    ? $"{scope.Name} is unticked on this pack's own page, so it is left alone there."
                    : $"It is in {scope.Name} but not switched on, so the game is not loading it.",
            FormatLabel = format is int pf ? $"pack_format {pf}" : "",
            FormatTip = format is int pf2
                ? ResourcePackFormats.Describe(pf2)
                  + (advisory is { Length: > 0 } ? "\n\n" + advisory : "")
                  + "\n\nAdvisory only - the launcher never refuses a pack over this, because the table "
                  + "it comes from goes out of date every time Mojang changes the asset layout."
                : "",
            // The switch is the pack's "on by default" bit. Turning it on in one instance is done from
            // the row menu or that instance's Resources tab.
            ToggleOn = isDefault,
            CanToggle = true,
            ToggleTip = isDefault
                ? $"Stop handing {item.DisplayName} out. Copies already in your instances stay where "
                  + "they are."
                : $"Hand {item.DisplayName} to every compatible instance, and switch it on there. "
                  + "Open its page to untick the instances you do not want it in.",
            CanOpen = true,
            Icon = meta.Icon
        };
    }

    private ContentRowState InstanceState(RpInstalled installed, RpModel model, PackSummary? scope)
    {
        var info = installed.Info;
        var meta = installed.Meta;
        var folder = FolderOf(info.Key);
        model.Facts[info.Key] = new RpRowFacts(1, info.Enabled ? 1 : 0, 0, info.SizeBytes,
                                               info.LastModified, meta.PackFormat);
        var advisory = ResourcePackFormats.Advisory(meta.PackFormat, installed.Pack.MinecraftVersion);

        var tooltip = new List<string> { info.DisplayName, info.FileName };
        tooltip.Add(info.Enabled
            ? $"On in {installed.Pack.Name} · #{info.Priority} in its stack - #1 is the pack that wins"
            : $"Off in {installed.Pack.Name} - the game is not loading it");
        tooltip.Add("Only in this instance so far. Switch it on here and every compatible instance "
                  + "gets it.");
        if (meta.PackFormat is int f) tooltip.Add(ResourcePackFormats.Describe(f));
        if (advisory is { Length: > 0 }) tooltip.Add(advisory);
        if (meta.Description is { Length: > 0 } description) tooltip.Add(description.ReplaceLineEndings(" "));

        return new ContentRowState
        {
            Key = info.Key,
            DisplayName = info.DisplayName,
            Item = null,
            PackId = info.SourcePackId,
            Path = info.FilePath,
            MetaLine = $"{info.FileName}  ·  {SizeLabel(info.SizeBytes)}  ·  only in {installed.Pack.Name}"
                     + $"  ·  changed {TimeFormat.Date(info.LastModified)}",
            CoverageLine = "in 1 instance",
            CoverageTip = $"Only in {installed.Pack.Name}"
                        + (info.Enabled ? $", switched on at #{info.Priority} in its stack." : ", switched off."),
            Tooltip = string.Join("\n", tooltip),
            IsDefault = false,
            HasOwnCopy = false,
            FolderLabel = folder ?? "",
            FolderTip = folder is null ? "" : $"Filed under {folder}",
            IsLocalOnly = info.IsLocal,
            LocalTip = $"In {installed.Pack.Name}'s unsynced local/ folder, so nobody else on that "
                     + "instance gets it.",
            OffHere = !info.Enabled,
            OffHereTip = $"Not switched on in {installed.Pack.Name}.",
            FormatLabel = meta.PackFormat is int pf ? $"pack_format {pf}" : "",
            FormatTip = meta.PackFormat is int pf2
                ? ResourcePackFormats.Describe(pf2) + (advisory is { Length: > 0 } ? "\n\n" + advisory : "")
                : "",
            // Same switch on a row with no library copy; turning it on makes one (ToggleRowAsync).
            ToggleOn = false,
            CanToggle = true,
            ToggleTip = $"Hand {info.DisplayName} to every compatible instance. The launcher takes its "
                      + $"own copy so it has something to place; {installed.Pack.Name} keeps the file "
                      + "it has.",
            CanOpen = true,
            Icon = meta.Icon
        };
    }

    /// <summary>The library file's own meta, falling back to an installed copy's.</summary>
    /// <remarks>Reading the library file opens its zip (memo-cached by
    /// <see cref="ResourcePackService.ReadMeta"/>). The remembered paint runs on the UI thread, so with
    /// <paramref name="cheap"/> this only uses the cached copy's meta and the real scan fills in the
    /// icon and format later.</remarks>
    private static ResourcePackMeta ResourcePackMetaFor(LibraryItem item, List<RpInstalled> present, bool cheap)
    {
        if (!cheap)
        {
            try
            {
                var own = ResourcePackService.ReadMeta(item.Path, item.IsFolder);
                if (own.HasAnything) return own;
            }
            catch (Exception ex) { AppLog.LogError(nameof(ResourcePacksView), ex); }
        }
        return present.Count > 0 ? present[0].Meta : ResourcePackMeta.Empty;
    }

    private static string SizeLabel(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024:0.#} MB"
        : bytes > 0 ? $"{bytes / 1024} KB" : "-";

    /// <summary>The user folder an item is filed under, or null.</summary>
    private static string? FolderOf(string key)
    {
        foreach (var (name, members) in App.State.Settings.ResourcePackFolders)
            if (members.Contains(key)) return name;
        return null;
    }

    // ── the scope box ────────────────────────────────────────────────────────

    /// <summary>Builds the instance scope selector, catching its own errors.</summary>
    /// <remarks>Runs inside the page refresh's try, whose catch reports a read failure and blanks the
    /// page. A UI bug here shouldn't do that.</remarks>
    private void RebuildScopeBox()
    {
        try { RebuildScopeBoxCore(); }
        catch (Exception ex)
        {
            // Leave whatever the box already had: a stale scope list still lets you pick.
            AppLog.LogError("resourcepacks.scope-box", ex);
        }
    }

    private void RebuildScopeBoxCore()
    {
        using (_filling.Hold())
        {
            var items = new List<RpScopeItem> { new(null, "All instances") };
            items.AddRange(_packs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new RpScopeItem(p.Id, p.Name)));

            var keep = items.FirstOrDefault(i => i.Id == _scopePackId) ?? items[0];
            _scopePackId = keep.Id;

            PackScopeBox.ItemsSource = items;
            PackScopeBox.DisplayMemberPath = nameof(RpScopeItem.Label);
            PackScopeBox.SelectedItem = keep;
        }
        UpdateScopeDependentTips();
    }

    private void OnPackScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy) return;
        _scopePackId = (PackScopeBox.SelectedItem as RpScopeItem)?.Id;
        // The conflict note belongs to the previous scope; clear it before the remembered paint shows
        // the new one.
        _conflictNote = null;
        UpdateScopeDependentTips();
        // The scope changes which packs are listed, so reload rather than re-filter.
        _ = LoadAsync();
    }

    /// <summary>Makes "Open folder" name the instance it will open.</summary>
    private void UpdateScopeDependentTips()
    {
        var pack = ScopedPack() ?? SoleInstance();
        OpenFolderButton.ToolTip = pack is null
            ? "Open an instance's resourcepacks folder - pick which one"
            : $"Open {pack.Name}'s resourcepacks folder";
    }

    private PackSummary? ScopedPack() =>
        _scopePackId is Guid id ? _packs.FirstOrDefault(p => p.Id == id) : null;

    private PackSummary? SoleInstance() => _packs.Count == 1 ? _packs[0] : null;

    // ── sort ─────────────────────────────────────────────────────────────────

    private void OnSortMenuOpened(object sender, RoutedEventArgs e) => SyncSortChecks();

    private void OnSortButtonClick(object sender, RoutedEventArgs e)
    {
        if (SortButton.ContextMenu is null) return;
        SortButton.ContextMenu.PlacementTarget = SortButton;
        SortButton.ContextMenu.Placement = PlacementMode.Bottom;
        SyncSortChecks();
        SortButton.ContextMenu.IsOpen = true;
    }

    private void OnSortMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        if (!Enum.TryParse<ResourcePackSortMode>(tag, out var parsed)) return;
        _sortMode = parsed;
        App.State.Settings.ResourcePackSortMode = parsed;
        App.State.Settings.Save();
        SyncSortChecks();
        ApplyFilter();
    }

    private void SyncSortChecks()
    {
        SortModifiedMenuItem.IsChecked = _sortMode == ResourcePackSortMode.Modified;
        SortNameMenuItem.IsChecked = _sortMode == ResourcePackSortMode.Name;
        SortSizeMenuItem.IsChecked = _sortMode == ResourcePackSortMode.Size;
        SortPackMenuItem.IsChecked = _sortMode == ResourcePackSortMode.Pack;

        // An icon-only button always announces the mode it is in.
        SortButton.ToolTip = "Sort resource packs: " + _sortMode switch
        {
            ResourcePackSortMode.Name => "Name",
            ResourcePackSortMode.Size => "Size",
            ResourcePackSortMode.Pack => "Instances using it",
            _ => "Last modified"
        };
    }

    // ── folders ──────────────────────────────────────────────────────────────

    /// <summary>Builds the folder chip strip, catching its own errors.</summary>
    /// <remarks>Runs inside the page refresh's try, whose catch reports a read failure and blanks the
    /// page. A UI bug here shouldn't do that.</remarks>
    private void RebuildFolders()
    {
        try { RebuildFoldersCore(); }
        catch (Exception ex)
        {
            AppLog.LogError("resourcepacks.folder-chips", ex);
            _folderChips.Clear();   // an empty strip beats a half-built one
        }
    }

    /// <summary>Builds the chips: All, every user folder, and the trailing "New folder".</summary>
    /// <remarks>
    /// <para>Same strip as the Instances page (<c>PackListView.RefreshFolderChips</c>): the chip
    /// filters the list, and rename and delete are on its right-click menu.</para>
    /// <para>An active folder that no longer exists (settings from another machine) is reset here,
    /// otherwise the list would be filtered by a chip that isn't shown.</para>
    /// <para><c>AppSettings.ResourcePackFolderRules</c> is no longer used but is kept so
    /// stored rules aren't lost.</para>
    /// </remarks>
    private void RebuildFoldersCore()
    {
        // MemberComparer so the chip counts match the rows each chip filters to.
        var known = _all.Select(r => r.Key).ToHashSet(MemberComparer);
        var chips = new List<RpFolderChipVm>
        {
            new("", "All", "", _all.Count, _activeFolder is null, "Every pack on this page")
        };

        foreach (var name in App.State.Settings.ResourcePackFolders.Keys
                     .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
        {
            var count = MemberCount(App.State.Settings.ResourcePackFolders[name], known);
            chips.Add(new RpFolderChipVm(name, name, "", count, _activeFolder == name,
                $"Packs you filed under {name}. Folders are a saved view - nothing moves on disk."));
        }

        chips.Add(new RpFolderChipVm(NewFolderChipId, "New folder", NewFolderGlyph, 0, false,
            "Create a folder to file packs under"));

        // Diffed rather than cleared; a Reset replays the chips' entrance animation on every keystroke.
        ListDiff.Apply(_folderChips, chips, c => c.Id, (kept, fresh) => kept.CopyFrom(fresh));

        if (_activeFolder is { Length: > 0 } active
            && !App.State.Settings.ResourcePackFolders.ContainsKey(active))
        {
            _activeFolder = null;
            foreach (var chip in _folderChips) chip.SetActive(chip.Id.Length == 0);
        }
    }

    /// <summary>The reserved-name rule, applied on create and on rename.</summary>
    /// <remarks>A leading colon marks the strip's pseudo-folders and <c>team:</c> marks a
    /// team folder in <see cref="PackListView"/>; a user folder named like either would
    /// be mistaken for one.</remarks>
    private static bool IsReservedFolderName(string name) =>
        name.StartsWith(':') || name.StartsWith("team:", StringComparison.OrdinalIgnoreCase);

    private async Task CreateFolderAsync(ContentRow? addThis)
    {
        var name = (await _shell.PromptAsync("Create folder", "Folder name"))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (IsReservedFolderName(name))
        {
            Fail("Names starting with ':' or 'team:' are reserved for the launcher's own chips.");
            return;
        }
        if (App.State.Settings.ResourcePackFolders.ContainsKey(name))
        {
            Fail("A folder with that name already exists.");
            return;
        }

        App.State.Settings.CreateResourcePackFolder(name);
        if (addThis is not null) App.State.Settings.AddResourcePackToFolder(name, addThis.Key);
        App.State.Settings.Save();

        _activeFolder = name;
        RebuildFolders();
        ApplyFilter();
        Okay(addThis is null
            ? $"Created the folder '{name}'."
            : $"Created the folder '{name}' and filed {addThis.DisplayName} in it.");
    }

    private void OnFolderChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RpFolderChipVm chip }) return;
        if (chip.Id == NewFolderChipId) { _ = CreateFolderAsync(null); return; }
        _activeFolder = string.IsNullOrEmpty(chip.Id) ? null : chip.Id;
        RebuildFolders();
        ApplyFilter();
    }

    /// <summary>All and New folder are computed views, not folders anybody can rename.</summary>
    private void OnFolderChipRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RpFolderChipVm chip } && !chip.IsUserFolder)
            e.Handled = true;
    }

    private static RpFolderChipVm? FolderFromMenu(object src)
    {
        if (src is not MenuItem leaf) return null;
        DependencyObject? p = leaf;
        while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
        return p is ContextMenu { PlacementTarget: FrameworkElement fx } && fx.DataContext is RpFolderChipVm row
            ? row
            : null;
    }

    private async void OnFolderRename(object sender, RoutedEventArgs e)
    {
        try
        {
            var chip = FolderFromMenu(sender);
            if (chip is null || !chip.IsUserFolder) return;

            var fresh = (await _shell.PromptAsync("Rename folder", "New name", chip.Id))?.Trim();
            if (string.IsNullOrEmpty(fresh) || fresh == chip.Id) return;
            if (IsReservedFolderName(fresh))
            {
                Fail("Names starting with ':' or 'team:' are reserved for the launcher's own chips.");
                return;
            }

            // Renames the members and rules maps together, keeping the folder's position in the strip.
            if (!App.State.Settings.RenameResourcePackFolder(chip.Id, fresh))
            {
                Fail("A folder with that name already exists.");
                return;
            }
            if (_activeFolder == chip.Id) _activeFolder = fresh;
            App.State.Settings.Save();

            RebuildFolders();
            ApplyFilter();
            Okay($"Renamed the folder to '{fresh}'.");
        }
        catch (Exception ex) { Fail("That folder could not be renamed.", ex); }
    }

    private async void OnFolderDelete(object sender, RoutedEventArgs e)
    {
        try
        {
            var chip = FolderFromMenu(sender);
            if (chip is null || !chip.IsUserFolder) return;

            var count = App.State.Settings.ResourcePackFolders.TryGetValue(chip.Id, out var members)
                ? members.Count : 0;
            if (!await AppDialog.ConfirmAsync(_shell, "Delete folder",
                    $"Delete the folder '{chip.Id}'?"
                    + (count > 0
                        ? $"\n\nThe {count} pack(s) filed in it stay exactly where they are - only the "
                          + "grouping goes."
                        : ""),
                    "Delete folder", "Cancel", danger: true))
                return;

            App.State.Settings.DeleteResourcePackFolder(chip.Id);
            if (_activeFolder == chip.Id) _activeFolder = null;
            App.State.Settings.Save();

            RebuildFolders();
            ApplyFilter();
            Okay($"Deleted the folder '{chip.Id}'.");
        }
        catch (Exception ex) { Fail("That folder could not be deleted.", ex); }
    }

    /// <summary>Takes a deleted pack's key out of every folder, so a folder cannot keep counting
    /// something that is gone.</summary>
    private static void ForgetFolderMembership(string key)
    {
        foreach (var members in App.State.Settings.ResourcePackFolders.Values)
            members.RemoveAll(k => MemberComparer.Equals(k, key));
    }

    // ── folder membership ────────────────────────────────────────────────────

    /// <summary>The comparer for every "is this key in that folder" question.</summary>
    /// <remarks>Case-insensitive because the keys contain file names.
    /// <c>AppSettings.AddResourcePackToFolder</c> de-dups ordinally, so old settings can hold
    /// case variants: <see cref="SetMember"/> removes every match and
    /// <see cref="MemberCount"/> counts them once.</remarks>
    private static readonly StringComparer MemberComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>The user folder the strip is filtered to, or null for All.</summary>
    private string? ActiveUserFolder() =>
        _activeFolder is { Length: > 0 } folder
        && !IsReservedFolderName(folder)
        && App.State.Settings.ResourcePackFolders.ContainsKey(folder)
            ? folder
            : null;

    /// <summary>One folder's member keys: the stored list itself, not a copy.</summary>
    private static List<string>? MemberKeys(string? folder) =>
        folder is { Length: > 0 }
        && App.State.Settings.ResourcePackFolders.TryGetValue(folder, out var list)
            ? list
            : null;

    /// <summary>Is this key filed under that folder?</summary>
    private static bool IsMember(IReadOnlyList<string>? members, string key) =>
        members is not null && members.Any(k => MemberComparer.Equals(k, key));

    /// <summary>Files a key under a folder, or takes it out, and saves.</summary>
    /// <remarks>Unlike the <see cref="AppSettings"/> mutators it calls, this saves right away: filing
    /// is a single menu click with no dialog to commit it.</remarks>
    private static void SetMember(string folder, string key, bool wanted)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(key)) return;
        if (wanted)
        {
            if (!IsMember(MemberKeys(folder), key)) App.State.Settings.AddResourcePackToFolder(folder, key);
        }
        else if (MemberKeys(folder) is { } list)
        {
            // Remove every match; see MemberComparer.
            list.RemoveAll(k => MemberComparer.Equals(k, key));
        }
        App.State.Settings.Save();
    }

    /// <summary>The count on a folder's chip: its members that have a row on this page.</summary>
    /// <remarks>Distinct with the filter's comparer, so case variants of one key count once.</remarks>
    private static int MemberCount(IReadOnlyList<string> members, HashSet<string> known) =>
        members.Distinct(MemberComparer).Count(known.Contains);

    /// <summary>Files this pack under a folder, or takes it out.</summary>
    /// <remarks>Only writes the folder list in settings.json: no file moves and no options.txt is
    /// touched, and the message says so. No reload needed; <see cref="ApplyFilter"/> recounts the
    /// chips.</remarks>
    private void FileIntoFolder(ContentRow row, string folder, bool wanted)
    {
        try
        {
            SetMember(folder, row.Key, wanted);
            ApplyFilter();
            Okay(wanted
                ? $"{row.DisplayName} is filed under {folder}. Nothing moved on disk - a folder is a "
                  + "saved view."
                : $"{row.DisplayName} is no longer filed under {folder}.");
        }
        catch (Exception ex) { Fail("That folder could not be changed.", ex); }
    }

    // ── filtering ────────────────────────────────────────────────────────────

    /// <summary>Narrows <see cref="_all"/> into the visible list and writes the page's count.</summary>
    /// <remarks>Diffed into the bound collection (<see cref="ListDiff"/>). Replacing it would reset the
    /// scroll position, selection and entrance animation on every keystroke.</remarks>
    private void ApplyFilter(bool announce = false)
    {
        RebuildFolders();

        IEnumerable<ContentRow> seq = _all;
        var filtered = false;

        // The folder chip just filters to its members, as on the Instances page.
        var folder = ActiveUserFolder();
        if (MemberKeys(folder) is { } members)
        {
            var set = new HashSet<string>(members, MemberComparer);
            seq = seq.Where(r => set.Contains(r.Key));
            filtered = true;
        }

        var query = SearchBox.Text.Trim();
        if (query.Length > 0)
        {
            filtered = true;
            seq = seq.Where(r => Matches(r, query));
        }

        ListDiff.Apply(PackList.Rows, Sorted(seq).ToList(), r => r.Key);

        if (_scopePackId is not null) filtered = true;

        // Only the load path sets the page state. A keystroke mid-scan must not replace "Scanning..."
        // with a count from a half-filled list.
        if (!announce && _state.Kind is PageStateKind.Loading or PageStateKind.Refreshing) return;

        if (PackList.Rows.Count == 0 && filtered && _all.Count > 0)
        {
            // Tell the empty panel whether the folder is empty or the search matched nothing.
            if (folder is { Length: > 0 } active && query.Length == 0)
                _state.EmptyNext($"Nothing in {active} yet",
                    "Right-click a pack on the All chip and pick 'Add to folder' to file it here. "
                    + "Nothing moves on disk - a folder is a saved view.",
                    glyph: "");
            else
                _state.EmptyFiltered();
        }

        // The conflict note is appended to the age note, not swapped for it: they answer different
        // questions.
        var lead = _cacheNote ?? _staleNote;
        var note = string.Join("  ·  ",
            new[] { lead, _conflictNote }.Where(s => s is { Length: > 0 }));
        _state.Content(PackList.Rows.Count, note: note.Length == 0 ? null : note);
    }

    private RpRowFacts FactsFor(ContentRow row) =>
        _model.Facts.TryGetValue(row.Key, out var facts)
            ? facts
            : new RpRowFacts(0, 0, 0, row.Item?.SizeBytes ?? 0,
                             row.Item?.LastModified ?? DateTimeOffset.MinValue);

    private static bool Matches(ContentRow row, string query)
    {
        var q = query.ToLowerInvariant();
        return row.DisplayName.ToLowerInvariant().Contains(q)
               || row.MetaLine.ToLowerInvariant().Contains(q)
               || row.Tooltip.ToLowerInvariant().Contains(q);
    }

    private IEnumerable<ContentRow> Sorted(IEnumerable<ContentRow> rows) =>
        _sortMode switch
        {
            ResourcePackSortMode.Name =>
                rows.OrderByDescending(r => r.IsDefault)
                    .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            ResourcePackSortMode.Size =>
                rows.OrderByDescending(r => r.IsDefault)
                    .ThenByDescending(r => FactsFor(r).SizeBytes)
                    .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            ResourcePackSortMode.Pack =>
                rows.OrderByDescending(r => r.IsDefault)
                    .ThenByDescending(r => FactsFor(r).Present)
                    .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ =>
                rows.OrderByDescending(r => r.IsDefault)
                    .ThenByDescending(r => FactsFor(r).Modified)
                    .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };

    // ── header actions ───────────────────────────────────────────────────────

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await LoadAsync(force: true); }
        catch (Exception ex) { Fail("The resourcepacks folders could not be re-read.", ex); }
    }

    private void OnOpenDownloadBrowser(object sender, RoutedEventArgs e) => _shell.OpenResourcePackBrowser();

    /// <summary>Opens the shared Import flow (<see cref="ImportContentCard"/>): pick the file, pick
    /// instances or none, optionally publish.</summary>
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try { await RunImportAsync(null); }
        catch (Exception ex) { Fail("That import did not finish.", ex); }
    }

    private async Task RunImportAsync(string? prePickedPath)
    {
        var outcome = await ImportContentCard.ShowAsync(_shell, new ImportRequest(
            ImportContentKind.ResourcePack, _packs,
            PreferredPackId: ScopedPack()?.Id ?? SoleInstance()?.Id,
            PrePickedPath: prePickedPath));
        if (outcome is null) return;

        foreach (var target in outcome.Targets) Cache.Invalidate(ScanCache<CachedRpRow>.ScopeKey(target));
        await LoadAsync(force: true);
        Okay(outcome.Summary);
    }

    /// <summary>Opens the instance whose stack the user wants to reorder.</summary>
    /// <remarks>Load order is one line in one instance's options.txt, so it is edited on that
    /// instance's Resources tab.</remarks>
    private void OpenLoadOrder(Guid packId, string packName)
    {
        _shell.OpenPackDetail(packId, packName);
        Okay($"Opened {packName} - the Resources tab holds its load order.");
    }

    /// <summary>Opens an instance's <c>resourcepacks</c> folder.</summary>
    /// <remarks>The scoped instance, or the only instance if there is one; otherwise it asks which.
    /// Same as the Worlds page's saves button.</remarks>
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var pack = ScopedPack() ?? SoleInstance();
        if (pack is not null) { OpenPacksFolder(pack); return; }
        if (_packs.Count == 0)
        {
            Fail("There is no instance to open yet.");
            return;
        }

        var menu = new ContextMenu { PlacementTarget = OpenFolderButton, Placement = PlacementMode.Bottom };
        foreach (var candidate in _packs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var target = candidate;
            menu.Items.Add(ContentMenu.Item($"Resource packs in {target.Name}", () => OpenPacksFolder(target)));
        }
        menu.IsOpen = true;
    }

    /// <summary>Create-then-open, so it works even if the folder doesn't exist yet.</summary>
    private void OpenPacksFolder(PackSummary pack)
    {
        try
        {
            var dir = Path.Combine(App.State.Packs.GameDir(pack.Id, pack.Name), "resourcepacks");
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) Fail("That folder could not be opened.");
        }
        catch (Exception ex) { Fail("That folder could not be opened.", ex); }
    }

    // ── row callbacks ────────────────────────────────────────────────────────

    /// <summary>Clicking a row opens the pack's own page.</summary>
    /// <remarks>Works for every row with a library item or an instance copy, since a newly imported
    /// pack has no copies until the next launch. An instance copy is preferred because it can show the
    /// pack's place in that instance's load order.</remarks>
    private void OpenRow(ContentRow row)
    {
        if (FirstInstalled(row) is { } installed)
        {
            _shell.OpenLocalResourcePackDetail(installed.Info.Key, row.DisplayName);
            return;
        }
        if (row.Item is { } item)
        {
            _shell.OpenLibraryResourcePackDetail(item, row.DisplayName);
            return;
        }
        // A stale row whose file the model no longer lists; just reveal it on disk.
        Reveal(row);
    }

    private RpInstalled? FirstInstalled(ContentRow row)
    {
        if (row.Item is null)
            return _model.Installed.FirstOrDefault(i => i.Info.Key == row.Key);

        var scope = _scopePackId;
        return _model.Installed
            .Where(i => string.Equals(i.Info.FileName, row.Item.FileName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => scope is Guid id && i.Pack.Id == id)
            .FirstOrDefault(i => SameFile(row.Item.Path, i.Info.FilePath, cheap: false));
    }

    /// <summary>The row's switch: whether this pack goes to every compatible instance.</summary>
    /// <remarks>Same meaning as the Worlds and Shaders switches. A row with no library copy is promoted
    /// first. Turning a pack on in a single instance is on the row menu and that instance's Resources
    /// tab.</remarks>
    private async Task ToggleRowAsync(ContentRow row, bool wanted)
    {
        try
        {
            if (row.RowClass == ContentRowClass.Library) { await SetDefaultAsync(row, wanted); return; }

            // A row with no library copy is never on, so there is nothing to turn off. Happens if the
            // row was rebuilt between press and release.
            if (!wanted) return;
            if (await PromoteAsync(row) is not { } item) return;
            // The promote reloaded the page, so look up the new row keyed on the library copy; the old
            // row's key is gone.
            if (RowFor(item.Key) is { } promoted) await SetDefaultAsync(promoted, true);
        }
        catch (Exception ex) { Fail("That pack could not be switched.", ex); }
    }

    /// <summary>The row now carrying an item's key, after a reload has rebuilt the list.</summary>
    private ContentRow? RowFor(string key) =>
        _all.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));

    /// <summary>Turns a pack on for every compatible instance, or off again.</summary>
    /// <remarks>
    /// <para>Instances are unticked on the pack's own page. <see cref="ContentDefaultPolicy.Activate"/>
    /// is set too, because a pack that is placed but not listed in <c>options.txt</c> is ignored by the
    /// game.</para>
    /// <para>Off only stops new placements; copies already placed stay where they are.</para>
    /// </remarks>
    private async Task SetDefaultAsync(ContentRow row, bool wanted)
    {
        try
        {
            if (row.Item is not { } item) return;

            var policy = App.State.Library.GetDefaults(item.Key);
            policy.Enabled = wanted;
            if (wanted) policy.Activate = true;

            // Placed packs stay on this machine unless the user says otherwise. resourcepacks/
            // is a synced folder, so otherwise one switch would push a personal pack to
            // everyone on every shared instance.
            var keepLocal = policy.KeepLocal || wanted;
            policy.KeepLocal = keepLocal;
            App.State.Library.SetDefaults(item.Key, policy);
            if (keepLocal) App.State.Library.SetKeepLocal(item.Key, true);

            await LoadAsync(force: true);
            // Only suggest opening its page once an instance has a copy; the page is built from one.
            Okay(wanted
                ? $"{row.DisplayName} is on: every compatible instance gets it, kept on this machine. "
                  + (FirstInstalled(row) is null
                      ? "Each one takes a copy the next time it starts - open its page after that to "
                        + "untick any of them."
                      : "Open its page to untick the ones you do not want it in.")
                : $"{row.DisplayName} is off. Copies already in your instances stay where they are.");
        }
        catch (Exception ex) { Fail("That pack could not be switched.", ex); }
    }

    /// <summary>Turns a pack on or off in one instance.</summary>
    /// <remarks>A newly enabled pack goes on top, as the game does when you select one.</remarks>
    private async Task SetEnabledAsync(RpInstalled installed, bool wanted)
    {
        if (!await ConfirmStackWriteAsync(installed.Pack)) return;

        var info = installed.Info;
        App.State.ResourcePacks.SetEnabled(info.SourcePackId, info.SourcePackName, info.FileName,
            wanted, installed.Pack.MinecraftVersion);

        Cache.Invalidate(ScanCache<CachedRpRow>.ScopeKey(info.SourcePackId));
        await LoadAsync(force: true);
        Okay(wanted
            ? $"{info.DisplayName} is on - top of {info.SourcePackName}'s stack."
            : $"{info.DisplayName} is off in {info.SourcePackName}.");
    }

    /// <summary>Minecraft reads options.txt at startup and rewrites it from memory on exit, so a change
    /// made under a running instance is thrown away when that instance closes.</summary>
    private async Task<bool> ConfirmStackWriteAsync(PackSummary pack)
    {
        if (!App.State.Instances.IsBusy(pack.Id)) return true;
        return await AppDialog.ConfirmAsync(_shell, "Minecraft is running",
            $"{pack.Name} is open. Minecraft rewrites options.txt when it closes, so this change would "
            + "be lost. Do it anyway?",
            "Do it anyway", "Cancel", danger: true);
    }

    // ── the row menu ─────────────────────────────────────────────────────────

    private const int IcOpen = 0xE7C3, IcDefault = 0xE73E, IcOn = 0xE768;
    private const int IcTop = 0xE74A, IcBottom = 0xE74B, IcOrder = 0xE71D, IcRename = 0xE8AC;
    private const int IcFile = 0xE8E5, IcReveal = 0xE8DA, IcFolder = 0xE8F1, IcShare = 0xE8EC;
    private const int IcCopy = 0xE8C8, IcLocal = 0xE753, IcDelete = 0xE74D;
    private const int IcMore = 0xE712, IcApply = 0xE895;

    /// <summary>The row's menu, built when it is opened.</summary>
    /// <remarks>Left-click on "..." gives the short list, right-click gives everything. Built on demand
    /// because a big ContextMenu in the item template would be built for every row.</remarks>
    private ContextMenu? BuildRowMenu(ContentRow row, bool full)
    {
        var scope = ScopedPack();
        var installed = FirstInstalled(row);
        var menu = ContentMenu.New(row.DisplayName,
            row.Item is null
                ? $"only in {installed?.Pack.Name ?? "one instance"}"
                : row.IsDefault
                    ? "on - every compatible instance gets it"
                    : "off - put in by hand");

        // Every row with an instance copy or a library item has a page.
        if (installed is not null || row.Item is not null)
            menu.Items.Add(ContentMenu.Item("Open pack page", () => OpenRow(row),
                ContentMenu.Glyph(IcOpen)));

        // Same as the row's switch, on every row. A row without a library copy is promoted first
        // (ToggleRowAsync -> PromoteAsync).
        menu.Items.Add(ContentMenu.Toggle("On everywhere", row.IsDefault, () =>
        {
            _ = ToggleRowAsync(row, !row.IsDefault);
            return !row.IsDefault;
        }, ContentMenu.Glyph(IcDefault)));

        // Per-instance on/off needs a single instance: the scope, or the row's own instance.
        if (installed is not null && (scope is null || installed.Pack.Id == scope.Id))
            menu.Items.Add(ContentMenu.Item(
                installed.Info.Enabled
                    ? $"Turn off in {installed.Pack.Name}"
                    : $"Turn on in {installed.Pack.Name}",
                () => _ = SetEnabledAsync(installed, !installed.Info.Enabled),
                ContentMenu.Glyph(IcOn)));

        // "Add to folder" is in the short menu too, as on the Instances page.
        menu.Items.Add(BuildFolderMenu(row));

        if (!full)
        {
            // A library row can always reveal its own copy, even before any instance has it.
            if (row.Path is { Length: > 0 })
                menu.Items.Add(ContentMenu.Item("Reveal in Explorer", () => Reveal(row), ContentMenu.Glyph(IcReveal)));
            menu.Items.Add(new Separator());
            menu.Items.Add(ContentMenu.Item("More options...",
                () => ContentMenu.ShowInstead(menu, () => BuildRowMenu(row, full: true)!),
                ContentMenu.Glyph(IcMore)));
            return menu;
        }

        // ── everything ──────────────────────────────────────────────────────
        if (installed is { Info.Enabled: true })
        {
            menu.Items.Add(ContentMenu.Item("Move to top of stack",
                () => _ = MoveAsync(installed, toTop: true), ContentMenu.Glyph(IcTop)));
            menu.Items.Add(ContentMenu.Item("Move to bottom of stack",
                () => _ = MoveAsync(installed, toTop: false), ContentMenu.Glyph(IcBottom)));
        }
        if (installed is not null)
            menu.Items.Add(ContentMenu.Item($"Load order for {installed.Pack.Name}...",
                () => OpenLoadOrder(installed.Pack.Id, installed.Pack.Name), ContentMenu.Glyph(IcOrder)));

        menu.Items.Add(new Separator());

        if (row.Item is { } libraryItem)
        {
            // The reconciler has no single-item mode, so this places every pack that is on.
            menu.Items.Add(ContentMenu.Item("Place everything that is on, including this",
                () => _ = ApplyOneAsync(row), ContentMenu.Glyph(IcApply),
                gesture: row.IsDefault ? null : "switched off", enabled: row.IsDefault));
            menu.Items.Add(BuildInstancesMenu(row, libraryItem));
            menu.Items.Add(ContentMenu.Toggle("Keep on this machine", libraryItem.KeepLocal, () =>
            {
                var wanted = !libraryItem.KeepLocal;
                App.State.Library.SetKeepLocal(libraryItem.Key, wanted);
                // Report after the reload, which resets the status line (see ReloadThenSayAsync).
                _ = ReloadThenSayAsync(wanted
                    ? $"{row.DisplayName} will never be uploaded from a shared instance."
                    : $"{row.DisplayName} will be uploaded with any shared instance it is in.");
                return wanted;
            }, ContentMenu.Glyph(IcLocal)));
        }

        menu.Items.Add(ContentMenu.Item("Rename...", () => _ = RenameAsync(row), ContentMenu.Glyph(IcRename),
            gesture: "F2"));
        if (installed is not null)
            menu.Items.Add(ContentMenu.Item("Rename file on disk...", () => _ = RenameFileAsync(installed),
                ContentMenu.Glyph(IcFile)));
        if (installed is not null)
            menu.Items.Add(ContentMenu.Item("Copy to another instance...", () => _ = CopyElsewhereAsync(row),
                ContentMenu.Glyph(IcCopy)));
        menu.Items.Add(ContentMenu.Item("Reveal in Explorer", () => Reveal(row), ContentMenu.Glyph(IcReveal)));
        menu.Items.Add(ContentMenu.Item("Copy key", () =>
        {
            if (ClipboardHelper.TrySetText(row.Key)) Okay("Copied key.");
        }));

        menu.Items.Add(new Separator());
        if (row.Item is not null)
            menu.Items.Add(ContentMenu.Item("Stop handing this out...", () => _ = RemoveFromLibraryAsync(row),
                ContentMenu.Glyph(IcDelete)));
        if (installed is not null)
            menu.Items.Add(ContentMenu.Item($"Delete from {installed.Pack.Name}...",
                () => _ = DeleteFromInstanceAsync(installed), ContentMenu.Glyph(IcDelete)));

        return menu;
    }

    /// <summary>The per-instance submenu: who has it, who could, and what is in the way.</summary>
    /// <remarks>Built on open because every entry checks the disk. Each state is spelled out (using the
    /// library copy, has its own copy, has an edited copy) since each needs a different fix. This is a
    /// one-off "put it there now"; which instances the pack is for is set on its own page.</remarks>
    private MenuItem BuildInstancesMenu(ContentRow row, LibraryItem item)
    {
        var parent = ContentMenu.Parent("Put it in...", ContentMenu.Glyph(IcShare));
        parent.SubmenuOpened += (_, _) =>
        {
            parent.Items.Clear();
            if (_packs.Count == 0)
            {
                parent.Items.Add(ContentMenu.Item("(no instances)", () => { }, enabled: false));
                return;
            }

            List<LibraryTargetState> states;
            try { states = _packs.Select(p => App.State.Library.Inspect(item, p)).ToList(); }
            catch (Exception ex)
            {
                AppLog.LogError(nameof(ResourcePacksView), ex);
                parent.Items.Add(ContentMenu.Item("(those instances could not be read)", () => { }, enabled: false));
                return;
            }

            foreach (var state in states.OrderBy(s => s.PackName, StringComparer.CurrentCultureIgnoreCase))
            {
                var target = _packs.First(p => p.Id == state.PackId);
                var applied = state.Applied;
                parent.Items.Add(ContentMenu.Toggle($"{state.PackName} - {state.StateLabel}", applied, () =>
                {
                    _ = applied ? UnapplyAsync(item, [target]) : ApplyAsync(item, [target], replaceOwnCopy: false);
                    return !applied;
                }));
            }

            parent.Items.Add(new Separator());
            parent.Items.Add(ContentMenu.Item("Every instance",
                () => _ = ApplyAsync(item, _packs, replaceOwnCopy: false)));

            // Only offered when it applies. It deletes the instance's own file, which on a shared
            // instance removes it for everyone.
            if (states.Any(s => s.HasOwnCopy))
                parent.Items.Add(ContentMenu.Item("Replace the instances' own copies...", () => _ = ReplaceOwnAsync(item, states)));

            // Re-linking compares whole files, so it's only offered for zips, not pack folders.
            if (!item.IsFolder && states.Any(s => s.Diverged))
                parent.Items.Add(ContentMenu.Item("Re-link the instances that drifted", () => _ = RelinkAsync(item, states)));
        };
        parent.Items.Add(ContentMenu.Item("(reading...)", () => { }, enabled: false));
        return parent;
    }

    private MenuItem BuildFolderMenu(ContentRow row)
    {
        var parent = ContentMenu.Parent("Add to folder", ContentMenu.Glyph(IcFolder));
        parent.SubmenuOpened += (_, _) =>
        {
            parent.Items.Clear();
            foreach (var name in App.State.Settings.ResourcePackFolders.Keys
                         .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
            {
                var folder = name;
                // Same helper as the chip count, so the two agree.
                var inside = IsMember(MemberKeys(folder), row.Key);
                parent.Items.Add(ContentMenu.Toggle(folder, inside, () =>
                {
                    FileIntoFolder(row, folder, !inside);
                    return !inside;
                }));
            }
            // Matches the Instances page's "Add to folder" menu (PackListView.OnCtxAddToFolderOpened).
            if (App.State.Settings.ResourcePackFolders.Count == 0)
                parent.Items.Add(ContentMenu.Item("(no folders - create one first)", () => { },
                    enabled: false));

            parent.Items.Add(new Separator());
            parent.Items.Add(ContentMenu.Item("Create new folder...", () => _ = CreateFolderAsync(row)));
        };
        parent.Items.Add(ContentMenu.Item("(reading...)", () => { }, enabled: false));
        return parent;
    }

    // ── row actions ──────────────────────────────────────────────────────────

    /// <summary>Gives the launcher its own copy of a pack that only exists in one instance, leaving
    /// that instance on the same file.</summary>
    /// <remarks>
    /// <para>The only promote path on this page. It goes through
    /// <see cref="ContentDefaultsService.AddFromInstanceAsync"/>, which handles two packs sharing a
    /// file name. No confirmation: the hard link is free, the source instance is untouched, and
    /// switching off undoes it.</para>
    /// <para>Returns the new library item, or null if nothing could be copied. The page has reloaded by
    /// then, so callers look the new row up by key.</para>
    /// </remarks>
    private async Task<LibraryItem?> PromoteAsync(ContentRow row)
    {
        try
        {
            var installed = FirstInstalled(row);
            if (installed is null) { Fail("That pack is no longer where it was - try Refresh."); return null; }

            // Only mention a copy where hard links aren't supported and the file really is duplicated.
            _state.Begin(App.State.Library.SupportsHardLinks
                ? $"Turning on {installed.Info.DisplayName}..."
                : $"Turning on {installed.Info.DisplayName} - copying it, because this drive cannot "
                  + "share one file between instances...",
                refreshing: true);
            var result = await App.State.ContentDefaults.AddFromInstanceAsync(
                installed.Pack,
                [new ContentAddRequest(installed.Info.FilePath, LibraryKind.ResourcePack,
                    installed.Info.DisplayName, PackFormat: installed.Meta.PackFormat)],
                makeDefault: false, policyTemplate: null,
                new Progress<string>(_state.Progress), _scanCts?.Token ?? default);

            Cache.Invalidate(ScanCache<CachedRpRow>.ScopeKey(installed.Pack.Id));
            await LoadAsync(force: true);
            // Refused because the game is running there. Nothing was attempted, so report a failure.
            if (result.Refused is { Length: > 0 } refused) { Fail(refused); return null; }
            Okay(result.Summary());
            return result.Items.FirstOrDefault()?.Item;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Fail("That pack could not be switched on.", ex); return null; }
    }

    /// <summary>Runs the reconciler now, placing everything that is on, this row included.</summary>
    /// <remarks>There is no single-item reconcile: plans are built per instance over the whole library,
    /// since the pack stack only makes sense with everything that is on considered together.</remarks>
    private async Task ApplyOneAsync(ContentRow row)
    {
        if (row.Item is null) return;
        try
        {
            _state.Begin($"Placing everything that is on, {row.DisplayName} included...", refreshing: true);
            var result = await App.State.ContentDefaults.ReconcileAllAsync(
                _packs, activate: true, new Progress<string>(_state.Progress), _scanCts?.Token ?? default);
            foreach (var pack in _packs) Cache.Invalidate(ScanCache<CachedRpRow>.ScopeKey(pack.Id));
            await LoadAsync(force: true);
            Okay(result.Summary());
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail("Those packs could not be placed.", ex); }
    }

    private async Task ApplyAsync(LibraryItem item, IReadOnlyList<PackSummary> targets, bool replaceOwnCopy)
    {
        try
        {
            _state.Begin($"Putting {item.FileName} into {targets.Count} instance(s)...", refreshing: true);
            var result = await App.State.Library.ApplyAsync([item], targets, replaceOwnCopy);
            foreach (var target in targets) Cache.Invalidate(ScanCache<CachedRpRow>.ScopeKey(target.Id));
            await LoadAsync(force: true);
            Okay(result.Summary());
        }
        catch (Exception ex) { Fail("That pack could not be shared.", ex); }
    }

    private async Task UnapplyAsync(LibraryItem item, IReadOnlyList<PackSummary> targets)
    {
        try
        {
            var result = await App.State.Library.UnapplyAsync([item], targets);
            foreach (var target in targets) Cache.Invalidate(ScanCache<CachedRpRow>.ScopeKey(target.Id));
            await LoadAsync(force: true);
            Okay(result.Summary());
        }
        catch (Exception ex) { Fail("That pack could not be taken out of the instance.", ex); }
    }

    private async Task ReplaceOwnAsync(LibraryItem item, List<LibraryTargetState> states)
    {
        var shadowed = states.Where(s => s.HasOwnCopy)
            .Select(s => _packs.First(p => p.Id == s.PackId)).ToList();
        if (shadowed.Count == 0) return;

        if (!await AppDialog.ConfirmAsync(_shell, "Use the shared copy",
                $"{shadowed.Count} instance(s) have their own copy of {item.FileName}, and that copy is the "
                + "one the game loads.\n\nDelete those copies so the shared one is used instead? If other "
                + "people are on one of those instances, this removes the file for them too.",
                "Use the shared copy", "Cancel", danger: true))
            return;
        await ApplyAsync(item, shadowed, replaceOwnCopy: true);
    }

    private async Task RelinkAsync(LibraryItem item, List<LibraryTargetState> states)
    {
        var drifted = states.Where(s => s.Diverged).Select(s => _packs.First(p => p.Id == s.PackId)).ToList();
        if (drifted.Count == 0) return;
        try
        {
            var result = await App.State.Library.RelinkAsync(item, drifted);
            foreach (var target in drifted) Cache.Invalidate(ScanCache<CachedRpRow>.ScopeKey(target.Id));
            await LoadAsync(force: true);
            Okay(result.Summary());
        }
        catch (Exception ex) { Fail("Those copies could not be re-linked.", ex); }
    }

    private async Task RemoveFromLibraryAsync(ContentRow row)
    {
        if (row.Item is not { } item) return;
        try
        {
            if (!await AppDialog.ConfirmAsync(_shell, "Stop handing this out",
                    $"Stop the launcher handing {item.DisplayName} to your instances?\n\n"
                    + $"The {item.AppliedTo.Count} instance(s) using it keep working - the file survives as "
                    + "long as any instance still holds it. Nothing will place it in a new instance again.",
                    "Stop handing it out", "Cancel", danger: true))
                return;

            await App.State.Library.RemoveAsync(item.Key);
            // The folder map is keyed on the item key, so a folder would otherwise keep counting
            // something that no longer exists.
            ForgetFolderMembership(item.Key);
            App.State.Settings.Save();
            await LoadAsync(force: true);
            Okay($"{item.DisplayName} is no longer handed to your instances.");
        }
        catch (Exception ex) { Fail("That item could not be removed.", ex); }
    }

    private async Task MoveAsync(RpInstalled installed, bool toTop)
    {
        try
        {
            if (!installed.Info.Enabled) return;
            if (!await ConfirmStackWriteAsync(installed.Pack)) return;

            // A delta past either end is clamped by the service, so "very far" is how you say top.
            App.State.ResourcePacks.MoveInStack(installed.Info.SourcePackId, installed.Info.SourcePackName,
                installed.Info.FileName, toTop ? -9999 : 9999);
            Cache.Invalidate(ScanCache<CachedRpRow>.ScopeKey(installed.Pack.Id));
            await LoadAsync(force: true);
            Okay(toTop
                ? $"{installed.Info.DisplayName} now overrides everything else in {installed.Pack.Name}."
                : $"{installed.Info.DisplayName} is now the first thing everything else overrides.");
        }
        catch (Exception ex) { Fail("That stack could not be rewritten.", ex); }
    }

    /// <summary>Renames the label the launcher shows. The file keeps its name; "Rename file on
    /// disk..." is a separate command.</summary>
    private async Task RenameAsync(ContentRow row)
    {
        try
        {
            var name = (await _shell.PromptAsync("Rename resource pack", "Display name", row.DisplayName))?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            if (row.Item is { } item) App.State.Library.Rename(item.Key, name);
            else
            {
                App.State.ResourcePacks.Rename(row.Key, name);
                if (row.PackId is Guid packId) Cache.Invalidate(ScanCache<CachedRpRow>.ScopeKey(packId));
            }
            await LoadAsync(force: true);
            Okay($"Renamed to {name}.");
        }
        catch (Exception ex) { Fail("That pack could not be renamed.", ex); }
    }

    /// <summary>Renames the zip (or pack folder), carrying the launcher's entry and the instance's
    /// options.txt line with it.</summary>
    /// <remarks>The settings key contains the file name, so without the re-key the pack would lose its
    /// display name, folder membership and hosting link. options.txt lists packs by file name too, so
    /// an enabled pack has to be renamed there or it gets switched off.</remarks>
    private async Task RenameFileAsync(RpInstalled installed)
    {
        try
        {
            var typed = await _shell.PromptAsync("Rename file on disk", "File name", installed.Info.FileName);
            if (typed is null) return;

            var wanted = ResourcePackService.SanitizeFileName(typed, installed.Info.IsFolder);
            if (string.Equals(wanted, installed.Info.FileName, StringComparison.Ordinal)) return;
            if (!PathSafety.IsSafeFileName(wanted))
            {
                Fail($"'{wanted}' cannot be used as a file name.");
                return;
            }
            if (installed.Info.Enabled && !await ConfirmStackWriteAsync(installed.Pack)) return;

            var info = installed.Info;
            await Task.Run(() => App.State.ResourcePacks.RenameFileOnDisk(info, wanted));
            Cache.Invalidate(ScanCache<CachedRpRow>.ScopeKey(installed.Pack.Id));
            await LoadAsync(force: true);
            Okay($"Renamed to {wanted}.");
        }
        catch (Exception ex) { Fail("That file could not be renamed.", ex); }
    }

    private void Reveal(ContentRow row)
    {
        try
        {
            var path = row.Path ?? FirstInstalled(row)?.Info.FilePath;
            if (path is null || (!File.Exists(path) && !Directory.Exists(path)))
            {
                Fail("That file is no longer there.");
                return;
            }
            if (!SafeLaunch.RevealFile(path)) Fail("Explorer could not be opened.");
        }
        catch (Exception ex) { Fail("Explorer could not be opened.", ex); }
    }

    private async Task CopyElsewhereAsync(ContentRow row)
    {
        try
        {
            var path = row.Path ?? FirstInstalled(row)?.Info.FilePath;
            if (path is null) { Fail("That file is no longer there."); return; }

            var from = FirstInstalled(row)?.Pack.Id;
            var targets = _packs.Where(p => p.Id != from).ToList();
            if (targets.Count == 0) { Fail("There is no other instance to copy it into."); return; }

            var pick = new PackPickerDialog(targets, "Copy resource pack", row.DisplayName, "Copy") { Owner = _shell };
            if (pick.ShowDialog() != true || pick.SelectedPackId is not Guid gid) return;

            await CopyIntoAsync([path], targets.First(p => p.Id == gid));
        }
        catch (Exception ex) { Fail("That pack could not be copied.", ex); }
    }

    private async Task DeleteFromInstanceAsync(RpInstalled installed)
    {
        try
        {
            var info = installed.Info;
            var what = info.IsFolder ? "pack folder" : "zip";
            var extra = info.Enabled
                ? "\n\nIt is turned on, so it will be removed from that instance's stack too."
                : "";
            var fromLibrary = _model.Library.Any(i =>
                string.Equals(i.FileName, info.FileName, StringComparison.OrdinalIgnoreCase)
                && SameFile(i.Path, info.FilePath, cheap: false));
            if (fromLibrary)
                extra += "\n\nThis is the shared copy, linked in here - the pack itself stays and every "
                       + "other instance using it is unaffected.";

            if (!await AppDialog.ConfirmAsync(_shell, "Delete resource pack",
                    $"Permanently delete the {what} '{info.FileName}' from {info.SourcePackName}?{extra}",
                    "Delete", "Cancel", danger: true))
                return;

            await Task.Run(() => App.State.ResourcePacks.DeleteFromDisk(info));
            ForgetFolderMembership(info.Key);
            App.State.Settings.Save();
            Cache.Invalidate(ScanCache<CachedRpRow>.ScopeKey(info.SourcePackId));
            await LoadAsync(force: true);
            Okay($"Deleted {info.FileName} from {info.SourcePackName}.");
        }
        catch (Exception ex) { Fail("That pack could not be deleted.", ex); }
    }

    // ── drag and drop ────────────────────────────────────────────────────────

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedPacks(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Dropping pack files opens the Import flow with the file already chosen
    /// (<see cref="ImportRequest.PrePickedPath"/>).</summary>
    /// <remarks>One file at a time, since the card walks one file through picking instances and
    /// publishing.</remarks>
    private async void OnListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            var files = DroppedPacks(e);
            if (files.Count == 0) return;
            if (files.Count > 1)
                Okay($"Importing {Path.GetFileName(files[0])} - drop them one at a time to give each its own "
                   + "instances.");
            await RunImportAsync(files[0]);
        }
        catch (Exception ex) { Fail("Those files could not be imported.", ex); }
    }

    /// <summary>The resource packs in a drag payload: zips, and folders holding a pack.mcmeta. Anything
    /// else is ignored rather than copied into resourcepacks/.</summary>
    private static List<string> DroppedPacks(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new();
        return (e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>())
            .Where(f => (File.Exists(f) && f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        || (Directory.Exists(f) && File.Exists(Path.Combine(f, "pack.mcmeta"))))
            .ToList();
    }

    /// <summary>Copies pack files straight into one instance's resourcepacks folder.</summary>
    /// <remarks>Only used by "copy to another instance"; files from outside go through
    /// <see cref="ImportContentCard"/>.</remarks>
    private async Task CopyIntoAsync(IReadOnlyList<string> sources, PackSummary target)
    {
        _state.Begin($"Copying into {target.Name}...", refreshing: true);
        var copied = await Task.Run(() =>
        {
            var dir = RpFolder(target);
            var n = 0;
            foreach (var source in sources)
            {
                var isFolder = Directory.Exists(source);
                var dest = BumpPath(dir, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)), isFolder);
                if (isFolder) CopyDirectory(source, dest);
                else File.Copy(source, dest);
                n++;
            }
            return n;
        });

        Cache.Invalidate(ScanCache<CachedRpRow>.ScopeKey(target.Id));
        await LoadAsync(force: true);
        Okay($"Copied {copied} pack(s) into {target.Name} - turn one on from its row.");
    }

    private static string RpFolder(PackSummary p)
    {
        App.State.Packs.EnsurePackFolder(p.Id, p.Name, p.IsShared);
        var dir = Path.Combine(App.State.Packs.GameDir(p.Id, p.Name), "resourcepacks");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A destination path that collides with nothing, for a zip or for an unpacked pack
    /// folder.</summary>
    private static string BumpPath(string dir, string name, bool isFolder)
    {
        var clean = ResourcePackService.SanitizeFileName(Path.GetFileName(name), isFolder);
        var candidate = Path.Combine(dir, clean);
        var stem = isFolder ? clean : Path.GetFileNameWithoutExtension(clean);
        var suffix = isFolder ? "" : ".zip";

        var n = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate))
            candidate = Path.Combine(dir, $"{stem}-{n++}{suffix}");
        return candidate;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest, StringComparison.Ordinal));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest, StringComparison.Ordinal), overwrite: true);
    }

    // ── keyboard ─────────────────────────────────────────────────────────────

    private async void GlobalKeys(object sender, KeyEventArgs e)
    {
        // The hub can be open in the side panel while this page still exists behind it.
        if (!IsVisible) return;

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await LoadAsync(force: true);
            return;
        }

        // Rows don't take keyboard focus, so these act on the row the user last touched.
        if (PackList.Focused is not { } row) return;
        if (SearchBox.IsExpanded && SearchBox.IsKeyboardFocusWithin) return;
        // Another page's text box (the side panel) can have the keyboard while this page is visible.
        if (TypingFocus.IsTyping) return;

        try
        {
            switch (e.Key)
            {
                case Key.Delete:
                    e.Handled = true;
                    if (row.Item is not null) await RemoveFromLibraryAsync(row);
                    else if (FirstInstalled(row) is { } installed) await DeleteFromInstanceAsync(installed);
                    break;
                case Key.F2:
                    e.Handled = true;
                    await RenameAsync(row);
                    break;
                case Key.Enter:
                    e.Handled = true;
                    OpenRow(row);
                    break;
            }
        }
        catch (Exception ex) { Fail("That did not work.", ex); }
    }

    // ── status line ──────────────────────────────────────────────────────────

    private void Okay(string message) => _state.Note(message);

    /// <summary>Reloads, then says what the action did.</summary>
    /// <remarks>A load ends in <c>PageState.Content</c>, which resets the status line, so a message
    /// written before the reload would never be seen.</remarks>
    private async Task ReloadThenSayAsync(string message)
    {
        try { await LoadAsync(force: true); }
        finally { Okay(message); }
    }

    /// <summary>Shows a short error; PageState logs the exception rather than showing it.</summary>
    private void Fail(string message, Exception? ex = null) => _state.Error(message, ex);
}

// ── view models ──────────────────────────────────────────────────────────────

/// <summary>One chip in the folder strip.</summary>
/// <remarks>No brushes: the selected look is a trigger on <see cref="IsActive"/>, so an accent change
/// repaints chips already on screen. Mutable and notifying because the strip is diffed rather than
/// rebuilt.</remarks>
public sealed class RpFolderChipVm : System.ComponentModel.INotifyPropertyChanged
{
    private string _countLabel;
    private bool _isActive;

    /// <param name="id">The folder name, "" for All, or the page's reserved <c>:new</c> id.</param>
    /// <param name="label">What the chip says.</param>
    /// <param name="icon">An MDL2 glyph, or empty.</param>
    /// <param name="count">How many rows are in it; zero shows no count.</param>
    /// <param name="isActive">True for the chip the page is currently filtered by.</param>
    /// <param name="tip">The chip's tooltip.</param>
    public RpFolderChipVm(string id, string label, string icon, int count, bool isActive, string tip)
    {
        Id = id;
        Label = label;
        Icon = icon;
        ToolTipText = tip;
        _countLabel = count > 0 ? $"({count})" : "";
        _isActive = isActive;
    }

    /// <inheritdoc cref="RpFolderChipVm(string,string,string,int,bool,string)"/>
    public string Id { get; }
    public string Label { get; }
    public string Icon { get; }

    /// <summary>The chip's tooltip. Fixed, since only the count can change.</summary>
    public string ToolTipText { get; }

    /// <summary>"(4)", or empty when there is nothing in it.</summary>
    public string CountLabel
    {
        get => _countLabel;
        private set { if (_countLabel != value) { _countLabel = value; Raise(nameof(CountLabel)); } }
    }

    /// <summary>True for the chip the page is filtered by.</summary>
    public bool IsActive
    {
        get => _isActive;
        private set { if (_isActive != value) { _isActive = value; Raise(nameof(IsActive)); } }
    }

    /// <summary>All and the trailing New folder chip are computed views, not folders anybody can
    /// rename.</summary>
    public bool IsUserFolder => Id.Length > 0 && !Id.StartsWith(':');

    /// <summary>True for the trailing "New folder" chip, which makes a folder rather than
    /// selecting one. The shared chip style triggers on this to draw it as an outline.</summary>
    public bool IsNewAction => Id == ":new";

    /// <summary>Copies a freshly built chip into this one, for <see cref="ListDiff.Apply"/>.</summary>
    /// <remarks>Copy everything that can change here, or that value stops updating once the chip is on
    /// screen.</remarks>
    public void CopyFrom(RpFolderChipVm fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        CountLabel = fresh.CountLabel;
        IsActive = fresh.IsActive;
    }

    /// <inheritdoc cref="IsActive"/>
    public void SetActive(bool active) => IsActive = active;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>One entry in the instance scope box.</summary>
public sealed record RpScopeItem(Guid? Id, string Label);
