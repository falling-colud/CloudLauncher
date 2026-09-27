using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// Every world on this PC, in a list, grouped into folders you make.
/// </summary>
/// <remarks>
/// <para>One row per world: all copies of a save share one <c>AutoApply</c> bit, so they share one
/// row and one switch (see <see cref="WorldGroup"/>). The saves themselves are never shared, so
/// anything that writes to one picks a copy by name. Clicking a row opens
/// <see cref="WorldDetailView"/> in the right-hand pane.</para>
/// <para>Scanning is expensive, so the list is painted from <see cref="ScanCache{T}"/> first and
/// re-read underneath by <see cref="PageRefresh"/>. The page is kept alive between navigations
/// (<see cref="IReusablePage"/>), so global subscriptions are made on Loaded and dropped on
/// Unloaded.</para>
/// </remarks>
public partial class WorldsView : Page, IReusablePage, IRefreshablePage
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<WorldFolderChipVm> _folderChips = new();

    /// <summary>Everything the scan found. <see cref="ApplyFilter"/> narrows this into the visible
    /// list, diffing instead of replacing (see <see cref="ListDiff"/>).</summary>
    private readonly ObservableCollection<ContentRow> _all = new();

    /// <summary>Guards the scope ComboBox against its own fill: assigning ItemsSource and then
    /// SelectedItem raises SelectionChanged synchronously, over a list nobody has filled yet.</summary>
    private readonly Reentrancy _filling = new();

    private readonly PageState _state;

    /// <summary>Cancels the scan in flight. Separate from <see cref="_busyCts"/>: cancelling a
    /// re-read must not cancel the copy that is halfway through a 12 GB save.</summary>
    private CancellationTokenSource? _scanCts;

    /// <summary>Cancels whichever long-running disk operation is in flight, if any.</summary>
    /// <remarks>Copies, zips and restores of a multi-gigabyte save are minute-scale, so every one of
    /// them runs under this and the status bar's Cancel button stops it.</remarks>
    private CancellationTokenSource? _busyCts;

    private List<PackSummary> _packs = new();
    private WorldModel _model = new();
    private WorldSortMode _sortMode;
    private string? _activeFolder;
    private Guid? _scopePackId;

    /// <summary>Why the instance list may be out of date (offline). <c>_cacheNote</c> holds the
    /// provenance line <see cref="PageRefresh"/> writes while a remembered scan is on screen; the filter
    /// re-applies both on every pass.</summary>
    private string? _staleNote;
    private string? _cacheNote;

    /// <summary>Instances we have seen running, so that a game closing can drop the remembered scan
    /// for the saves it just rewrote.</summary>
    private readonly HashSet<Guid> _running = new();

    /// <summary>The trailing chip that makes a new folder. It starts with a colon, which
    /// <see cref="IsReservedFolderName"/> refuses on create and on rename, so it can never collide
    /// with a folder the user made.</summary>
    private const string NewFolderChipId = ":new";

    /// <summary>The plus on the trailing "New folder" chip. It is an action among filters, so the
    /// shared chip style draws it outlined (FolderChipBorder in Themes/Controls.xaml).</summary>
    private const string NewFolderGlyph = "";

    /// <summary>
    /// Last session's scan, so opening this page does not start with a full recursive walk.
    /// </summary>
    /// <remarks>
    /// <c>WorldService</c> already caches sizes and metadata for the life of the process; this keeps
    /// them across launches, so the first open paints immediately and re-reads in the background.
    /// </remarks>
    private static readonly ScanCache<CachedWorld> Scans = ScanCaches.For<CachedWorld>(ScanKinds.Worlds);

    public WorldsView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        _sortMode = App.State.Settings.WorldSortMode;
        FolderStrip.ItemsSource = _folderChips;

        WorldList.Configure(new ContentListHost
        {
            Open = OpenRow,
            Toggled = ToggleRowAsync,
            // A switch, as on the Resource packs page: any number of worlds can be offered everywhere.
            // (The Shaders page uses a radio because an instance has one shader slot.) The control is
            // per page (ContentListView.ShowsRowToggle), so copies that share one bit have to be one
            // row; see WorldGroup.
            RowControl = ContentRowControl.Toggle,
            Menu = SaveMenu,
            Status = Okay
        });

        _state = new PageState(WorldList, PageStateHost, nameof(WorldsView))
            .Copy(PageCopy.Worlds)
            .Slots(CountLabel, StatusLabel, BusyBar, BusyCancelButton)
            .DisableWhileBusy(RefreshButton, SortButton, PackScopeBox);
        _state.EmptyCopy(
            "No worlds yet",
            "Import a world you already have, or find one in the store. It lands in the instances you "
            + "pick, and each of those is that instance's own world from the moment it is played.",
            "Import a world",
            () => OnImport(this, new RoutedEventArgs()));
        _state.RetryRequested += () => _ = LoadAsync(force: true);
        _state.CancelRequested += () =>
        {
            // Cancel stops whatever is running, a disk operation first: cancelling only the re-read
            // during a big copy would look like it had stopped the copy.
            if (_busyCts is { } busy)
            {
                busy.Cancel();
                _state.Cancelled("Stopping...");
                return;
            }
            _scanCts?.Cancel();
            _state.Cancelled("Scan stopped.");
        };

        SearchBox.TextChangedDebounced += (_, _) => ApplyFilter();

        // Subscribed on Loaded to pair with the Unloaded removals below. The page is reused
        // (IReusablePage), so subscribing in the constructor would leave these dead after the
        // first navigation.
        Loaded += async (_, _) =>
        {
            Window.GetWindow(this)!.PreviewKeyDown += GlobalKeys;
            App.State.Instances.StateChanged += OnInstanceStateChanged;
            App.State.ConnectivityChanged += OnConnectivityChanged;
            await LoadAsync();
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is Window w) w.PreviewKeyDown -= GlobalKeys;
            App.State.Instances.StateChanged -= OnInstanceStateChanged;
            App.State.ConnectivityChanged -= OnConnectivityChanged;
            _scanCts?.Cancel();
            _busyCts?.Cancel();
        };
    }

    // ── the scan's own model ─────────────────────────────────────────────────

    /// <summary>What one pass of the scan found. Assigned once, at the end of the worker.</summary>
    private sealed class WorldModel
    {
        /// <summary>Every save found on disk, across every instance.</summary>
        public List<WorldInfo> Saves { get; init; } = [];

        /// <summary>How many worlds the library is holding that no instance has a copy of. Counted by
        /// <see cref="CountLibraryOnly"/>; see <see cref="LibraryOnlyNote"/> for why they are a
        /// sentence in the status bar rather than rows.</summary>
        public int LibraryOnly { get; init; }

        /// <summary>Row key -> the numbers behind that row, so the sort doesn't have to parse the
        /// row's label text.</summary>
        public Dictionary<string, WorldRowFacts> Facts { get; } = new(StringComparer.Ordinal);

        /// <summary>Save key -> the launcher's own copy of that save, where it has one. A save with no
        /// entry here has never been handed out, so its switch is off.</summary>
        public Dictionary<string, LibraryItem> Templates { get; init; } = new(StringComparer.Ordinal);

        /// <summary>Row key -> the copies that row speaks for. Filled by <see cref="BuildRows"/>, and
        /// the only way back from a row to a save on disk (<see cref="GroupOf"/>).</summary>
        public Dictionary<string, WorldGroup> Groups { get; } = new(StringComparer.Ordinal);

        /// <summary>Each instance's content overrides, read here so the switch's "N instances would get
        /// one" matches the engine's own count.</summary>
        public Dictionary<Guid, InstanceContentOverrides> Overrides { get; init; } = new();

        public DateTimeOffset? ScannedUtc { get; init; }
    }

    /// <summary>The numbers the sort uses for one row.</summary>
    private sealed record WorldRowFacts(long SizeBytes, DateTimeOffset Modified, string Instance);

    /// <summary>
    /// One world: the save of that name, plus every copy of it sitting in another instance.
    /// </summary>
    /// <remarks>
    /// <para>The launcher's copy of a world is matched to saves by folder name only
    /// (<see cref="MapTemplates"/>), so same-named saves in any instance share one <c>AutoApply</c> bit
    /// and one row. That includes unrelated saves that happen to share a name, so row text talks about
    /// "saves of this name" and lists each copy's own size and date.</para>
    /// <para><see cref="Primary"/> is the copy the row speaks for: the most recently played, or the
    /// scoped instance's. It sets the name, icon and one-click actions but not the key, which would
    /// otherwise change whenever another copy is played.</para>
    /// </remarks>
    private sealed record WorldGroup(string Key, WorldInfo Primary, IReadOnlyList<WorldInfo> Copies)
    {
        /// <summary>Every copy's own save key; folder membership is stored under these.</summary>
        public IEnumerable<string> SaveKeys => Copies.Select(c => c.Key);

        /// <summary>This world's copy inside one instance, if that instance has one.</summary>
        public WorldInfo? In(Guid packId) => Copies.FirstOrDefault(c => c.SourcePackId == packId);

        /// <summary>The copies other than <see cref="Primary"/>, in the order they were found.</summary>
        public IEnumerable<WorldInfo> Others => Copies.Where(c => c.Key != Primary.Key);
    }

    /// <summary>
    /// The row's identity: one row per world, not per copy.
    /// </summary>
    /// <remarks>Derived from the save folder name, which is what <see cref="MapTemplates"/> matches
    /// library items on, so it groups the saves that share one switch. It can't collide with a save key
    /// (<c>WorldService.Key</c> starts with 32 hex digits and a colon), which matters because folder
    /// membership in settings.json is stored under save keys (see <see cref="MemberComparer"/>).</remarks>
    private static string WorldKeyOf(string folderName) => "world:" + folderName.ToLowerInvariant();

    // ── loading ──────────────────────────────────────────────────────────────

    /// <inheritdoc cref="IRefreshablePage.RefreshAsync"/>
    /// <remarks>What the offline banner's Retry and a reopen both ask for: re-read the instance list
    /// and re-run the scan, keeping the rows, the scroll position and the selection.</remarks>
    public Task RefreshAsync() => LoadAsync();

    /// <summary>
    /// Paints the remembered scan, then re-reads every saves folder underneath it.
    /// </summary>
    /// <param name="force">Ignore the remembered scan and re-walk every save. Used by Refresh/F5 (the
    /// only way to catch an edit that changed no file's size) and after this page writes inside a
    /// saves folder.</param>
    private async Task LoadAsync(bool force = false)
    {
        // A fresh token source every pass, so a reused page doesn't come back with a token that is
        // already cancelled.
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        var ok = await PageRefresh.RunAsync(
            _state, _all, WorldList.ListControl, r => r.Key,
            cached: () => force ? CachedRows<ContentRow>.None : Remembered(),
            rescan: token => Rescan(force, token),
            ct: ct,
            filter: () => ApplyFilter(announce: true),
            note: n => _cacheNote = n,
            update: (row, scanned) => row.CopyFrom(scanned),
            prepare: PrepareAsync,
            failed: "The saves folders could not be read.",
            cachedNote: age => age is { Length: > 0 }
                ? $"Remembered from the last scan ({age}) - re-reading the saves folders now."
                : "Showing the last scan - re-reading the saves folders now.",
            quiet: !force);

        if (!ok) return;

        if (_staleNote is { Length: > 0 } why) _state.Note(why);
    }

    /// <summary>
    /// Tells the user the library is holding worlds this page doesn't list.
    /// </summary>
    /// <remarks>A world that only exists in <c>.library/saves/</c> (an import with no instance ticked, a
    /// store download) has no row and would otherwise be invisible disk usage. Shares the slot with the
    /// offline note but never replaces it.</remarks>
    private string? LibraryOnlyNote() =>
        _model.LibraryOnly > 0
            ? $"{_model.LibraryOnly} world(s) have been brought in but are not in any instance, so they "
              + $"are not listed here. They are on disk at {App.State.Library.FolderFor(LibraryKind.World)}."
            : null;

    /// <summary>The cheap setup the remembered paint needs: the cache file, the instance list, and
    /// the scope box that is built from it.</summary>
    private async Task PrepareAsync(CancellationToken ct)
    {
        // Warm the cache file off the dispatcher so the first Get below is a dictionary lookup.
        await Scans.EnsureLoadedAsync();

        _staleNote = null;
        try
        {
            // Quick: the list the Instances page fetched moments ago, when there is one, so the
            // remembered rows paint in the same moment the page appears instead of after a round trip.
            _packs = await App.State.Api.ListPacksQuickAsync(ct);
            _staleNote = App.State.Api.PackListStale is { Length: > 0 } stale
                ? $"Showing saves for your last known instances - {stale}."
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // ListPacksAsync already falls back server -> cached list -> disk scan, so a throw means
            // there is no list at all. The saves are still on disk, so carry on instead of blanking
            // the page.
            AppLog.LogError(nameof(WorldsView), ex);
            _packs = PackListCache.Load() ?? new List<PackSummary>();
            _staleNote = "The instance list could not be refreshed; scanning the instances already known.";
        }

        // Instances already mid-transfer when the page opens, so their rows come up busy rather
        // than flickering into it a moment later.
        foreach (var pack in _packs)
        {
            if (App.State.Instances.IsBusy(pack.Id)) _running.Add(pack.Id);
        }

        RebuildScopeBox();
    }

    private CachedRows<ContentRow> Remembered()
    {
        try
        {
            var saves = new List<WorldInfo>();
            DateTimeOffset? oldest = null;

            foreach (var pack in _packs)
            {
                var hit = Scans.Get(ScanCache<CachedWorld>.ScopeKey(pack.Id));
                if (hit.State == ScanState.Cold) continue;

                string savesDir;
                try { savesDir = App.State.Worlds.SavesDir(pack.Id, pack.Name); } catch { continue; }

                // An instance whose saves folder is gone must not be repainted from memory: the
                // rows would name saves that are no longer on disk.
                if (!RootFingerprint.Shallow(savesDir).Exists) continue;

                foreach (var row in hit.Rows)
                    if (IsPlainSaveName(row)) saves.Add(Rehydrate(row, pack, savesDir));
                if (hit.ScannedUtc is { } at && (oldest is null || at < oldest)) oldest = at;
            }

            var ordered = saves.OrderByDescending(w => w.LastModified).ToList();
            if (ordered.Count == 0) return CachedRows<ContentRow>.None;

            var model = new WorldModel
            {
                Saves = ordered,
                LibraryOnly = CountLibraryOnly(ordered),
                Templates = MapTemplates(ordered),
                Overrides = LoadOverrides(_packs),
                ScannedUtc = oldest
            };
            // Build the rows before publishing the model: BuildRows fills Facts and Groups off the UI
            // thread, and a search keystroke on the UI thread reads both.
            var rows = BuildRows(model);
            _model = model;
            return new CachedRows<ContentRow>(rows, oldest);
        }
        catch (Exception ex)
        {
            // Nothing remembered is fine; the real scan is already on its way.
            AppLog.LogError(nameof(WorldsView), ex);
            return CachedRows<ContentRow>.None;
        }
    }

    /// <summary>The real read: every instance's saves folder, off the dispatcher. A cached scope
    /// whose fingerprint still matches is rehydrated instead of re-measured.</summary>
    /// <remarks>Runs on a worker thread via <see cref="PageRefresh"/>, so it must not touch the UI. It
    /// only reads the settings snapshot and the scan caches, and builds plain view models with frozen
    /// bitmaps, which are safe to hand to the UI thread.</remarks>
    private IReadOnlyList<ContentRow> Rescan(bool force, CancellationToken ct)
    {
        var saves = new List<WorldInfo>();

        foreach (var pack in _packs)
        {
            ct.ThrowIfCancellationRequested();

            // One try around the whole per-instance body (as in ResourcePacksView and ShaderPacksView),
            // so one unreadable instance doesn't cost every other instance's saves.
            try
            {
                var savesDir = App.State.Worlds.SavesDir(pack.Id, pack.Name);
                var scopeKey = ScanCache<CachedWorld>.ScopeKey(pack.Id);
                var stamp = FingerprintOf(savesDir, ct);

                if (!force)
                {
                    var hit = Scans.Get(scopeKey);
                    // Fingerprint intact: the rows we remembered are still what is on disk.
                    if (hit.State == ScanState.Cached && hit.Fingerprint == stamp && hit.Rows.All(IsPlainSaveName))
                    {
                        foreach (var row in hit.Rows) saves.Add(Rehydrate(row, pack, savesDir));
                        continue;
                    }
                }
                else
                {
                    // A forced re-read also has to clear WorldService's per-process cache, or Refresh
                    // would return the same sizes it was asked to re-measure.
                    foreach (var dir in WorldDirs(savesDir)) WorldService.Invalidate(dir);
                }

                var scanned = App.State.Worlds.ScanPack(pack, ct);
                saves.AddRange(scanned);
                Scans.Put(scopeKey, scanned.Select(ToCached).ToList(), stamp);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Log it: a folder that vanished mid-scan isn't worth an error panel, but it
                // shouldn't be invisible either.
                AppLog.LogError($"worlds.rescan:{pack.Name}", ex);
            }
        }

        var ordered = saves.OrderByDescending(w => w.LastModified).ToList();
        var model = new WorldModel
        {
            Saves = ordered,
            LibraryOnly = CountLibraryOnly(ordered),
            Templates = MapTemplates(ordered),
            Overrides = LoadOverrides(_packs),
            ScannedUtc = DateTimeOffset.UtcNow
        };
        // Build the rows before publishing, as in Remembered: BuildRows fills Facts and Groups on
        // this worker thread, and ApplyFilter reads both on the UI thread.
        var rows = BuildRows(model);
        _model = model;
        return rows;
    }

    /// <summary>
    /// Save key -> the launcher's own copy of that save, for the saves it has one of.
    /// </summary>
    /// <remarks>Matched by name, unlike the other content pages: a world is copied into the library, not
    /// linked, so the files diverge once either side is played. A library copy carries either the
    /// save's folder name or the zip's name, so both are tried.</remarks>
    private static Dictionary<string, LibraryItem> MapTemplates(List<WorldInfo> saves)
    {
        var map = new Dictionary<string, LibraryItem>(StringComparer.Ordinal);
        try
        {
            var library = App.State.Library.Scan(LibraryKind.World);
            if (library.Count == 0) return map;

            foreach (var save in saves)
            {
                var hit = library.FirstOrDefault(i =>
                    string.Equals(i.FileName, save.FolderName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileNameWithoutExtension(i.FileName), save.FolderName,
                                     StringComparison.OrdinalIgnoreCase));
                if (hit is not null) map[save.Key] = hit;
            }
        }
        catch (Exception ex) { AppLog.LogError(nameof(WorldsView), ex); }
        return map;
    }

    /// <summary>Each instance's content overrides: one small JSON read per instance. A missing file
    /// means nothing has been decided.</summary>
    private static Dictionary<Guid, InstanceContentOverrides> LoadOverrides(IReadOnlyList<PackSummary> packs)
    {
        var map = new Dictionary<Guid, InstanceContentOverrides>();
        foreach (var pack in packs)
        {
            try { map[pack.Id] = App.State.ContentDefaults.OverridesFor(pack.Id); }
            catch (Exception ex) { AppLog.LogError(nameof(WorldsView), ex); }
        }
        return map;
    }

    /// <summary>
    /// Worlds the library holds that no instance has a copy of.
    /// </summary>
    /// <remarks>A library world counts as placed if a save folder of its name exists somewhere, or if an
    /// instance records the launcher putting it there (<see cref="InstanceContentOverrides.WorldFolderFor"/>,
    /// which survives the copy being renamed). Both are checked, or the count would be too high.</remarks>
    private int CountLibraryOnly(List<WorldInfo> saves)
    {
        try
        {
            var library = App.State.Library.Scan(LibraryKind.World);
            if (library.Count == 0) return 0;

            var overrides = new List<InstanceContentOverrides>();
            foreach (var pack in _packs)
            {
                try { overrides.Add(App.State.ContentDefaults.OverridesFor(pack.Id)); }
                catch (Exception ex) { AppLog.LogError(nameof(WorldsView), ex); }
            }

            return library.Count(item =>
            {
                var candidate = item.IsFolder
                    ? item.FileName
                    : WorldService.SafeFolderName(Path.GetFileNameWithoutExtension(item.FileName));
                return !overrides.Any(o => o.WorldFolderFor(item.Key) is not null)
                       && !saves.Any(w => string.Equals(w.FolderName, candidate,
                                                        StringComparison.OrdinalIgnoreCase));
            });
        }
        catch (Exception ex) { AppLog.LogError(nameof(WorldsView), ex); return 0; }
    }

    /// <summary>
    /// What one instance's saves folder looks like, cheaply.
    /// </summary>
    /// <remarks>Depth 1 only, one directory read per world. New chunks show up through the level.dat
    /// stamp, since Minecraft rewrites level.dat on every save; an edit that changes no size and doesn't
    /// touch level.dat needs Refresh.</remarks>
    private static string FingerprintOf(string savesDir, CancellationToken ct)
    {
        var fp = new Fingerprint().Add("saves", RootFingerprint.Compute(savesDir, null, maxDepth: 1, ct));
        foreach (var dir in WorldDirs(savesDir))
        {
            ct.ThrowIfCancellationRequested();
            fp.Add(Path.GetFileName(dir), RootFingerprint.File(Path.Combine(dir, "level.dat")));
        }
        return fp.ToString();
    }

    /// <summary>The save folders of one instance, in a fixed order, since an unordered fingerprint
    /// would never match twice.</summary>
    private static List<string> WorldDirs(string savesDir)
    {
        try
        {
            return Directory.Exists(savesDir)
                ? Directory.GetDirectories(savesDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    private static CachedWorld ToCached(WorldInfo w) => new(
        w.FolderName, w.SizeBytes, w.LastModified,
        w.Info.LevelName, w.Info.Seed, w.Info.GameType, w.Info.Difficulty,
        w.Info.Hardcore, w.Info.AllowCommands, w.Info.VersionName, w.Info.LastPlayed);

    /// <summary>True when a remembered save's folder name is one plain name inside the saves folder.
    /// Delete and copy act on the path built from it, so any other row is dropped and the folder is
    /// read again.</summary>
    private static bool IsPlainSaveName(CachedWorld row)
    {
        if (PathSafety.IsSafeFileName(row.Folder)) return true;
        AppLog.Log(nameof(WorldsView), $"Ignored a remembered save with an unusable folder name: {row.Folder}");
        return false;
    }

    /// <summary>Turns a remembered row back into a <see cref="WorldInfo"/>. The display name is owned by
    /// the launcher and re-read from settings instead of cached, so renames show without a rescan.</summary>
    private static WorldInfo Rehydrate(CachedWorld row, PackSummary pack, string savesDir)
    {
        var key = WorldService.Key(pack.Id, row.Folder);
        var entry = App.State.Settings.Worlds.TryGetValue(key, out var e) ? e : null;
        var meta = new WorldMeta(row.LevelName, row.Seed, row.GameType, row.Difficulty,
                                 row.Hardcore, row.AllowCommands, row.VersionName, row.LastPlayed);

        // The legacy per-save compatibility list is still read and honoured by the play menu and
        // copy-to-instance, but it is no longer editable; the item's ContentDefaultPolicy replaces it.
        var compat = entry?.CompatiblePackIds.ToList() ?? new List<Guid>();
        if (!compat.Contains(pack.Id)) compat.Insert(0, pack.Id);

        return new WorldInfo(
            Key: key,
            SourcePackId: pack.Id,
            SourcePackName: pack.Name,
            FolderName: row.Folder,
            FolderPath: Path.Combine(savesDir, row.Folder),
            DisplayName: !string.IsNullOrEmpty(entry?.DisplayName) ? entry.DisplayName
                                                                  : meta.LevelName ?? row.Folder,
            LastModified: row.LastModified,
            SizeBytes: row.SizeBytes,
            CompatibleWithAll: entry?.CompatibleWithAll ?? false,
            CompatiblePackIds: compat,
            Meta: meta);
    }

    // ── building the rows ────────────────────────────────────────────────────

    /// <summary>
    /// One row per world (every copy of one save folded together), narrowed to the scoped instance
    /// when one is chosen.
    /// </summary>
    /// <remarks>A scope narrows without regrouping: rows are grouped over every save found, so "in 3
    /// instances" stays true with one instance picked. The scope only changes which rows are listed and
    /// which copy a row speaks for. See <see cref="WorldGroup"/>.</remarks>
    private List<ContentRow> BuildRows(WorldModel model)
    {
        var rows = new List<ContentRow>();
        var scope = ScopedPack();

        // Newest-first order in, newest-first order out: model.Saves is already sorted, so the first
        // copy of a world seen is the one played most recently and the groups come out in the order
        // their newest copy was touched.
        var order = new List<string>();
        var grouped = new Dictionary<string, List<WorldInfo>>(StringComparer.Ordinal);
        foreach (var save in model.Saves)
        {
            var key = WorldKeyOf(save.FolderName);
            if (grouped.TryGetValue(key, out var copies)) copies.Add(save);
            else { grouped[key] = [save]; order.Add(key); }
        }

        model.Groups.Clear();
        foreach (var key in order)
        {
            var copies = grouped[key];
            var primary = copies[0];

            if (scope is not null)
            {
                // Only worlds this instance holds, and the row speaks for its copy, so renaming from a
                // scoped page renames the save you are looking at.
                var here = copies.FirstOrDefault(c => c.SourcePackId == scope.Id);
                if (here is null) continue;
                primary = here;
            }

            var group = new WorldGroup(key, primary, copies);
            model.Groups[key] = group;
            rows.Add(new ContentRow(GroupState(group, model)));
        }

        return rows;
    }

    /// <summary>One world: the save this row speaks for, and every other save of that name.</summary>
    /// <remarks>Most text below has two forms, one for a world in a single instance and one for a
    /// folded world, so the row never implies the copies are one file.</remarks>
    private ContentRowState GroupState(WorldGroup group, WorldModel model)
    {
        var save = group.Primary;
        var info = save.Info;
        var folder = FolderOf(group);

        // The copies in other instances. Empty for almost every world, in which case the row is
        // the plain single-save row.
        var others = group.Others.ToList();

        // Every instance this world sits in, not just this row's: the switch reaches all of them, so
        // a game running in any one of them is a reason not to write anything.
        var busy = group.Copies.Where(c => App.State.Instances.IsBusy(c.SourcePackId))
                               .Select(c => c.SourcePackName).Distinct().ToList();
        var running = busy.Count > 0;

        var totalBytes = group.Copies.Sum(c => c.SizeBytes);

        // What the sort reads. The size is the total, matching what the row shows. The date is the
        // newest copy's, and the instance is this row's own copy's (the scoped one when a scope is
        // picked).
        model.Facts[group.Key] = new WorldRowFacts(
            totalBytes, group.Copies.Max(c => c.LastModified), save.SourcePackName);

        // The launcher's own copy of this world, if it has one; this is what the switch controls.
        // Without one nothing has been handed out yet, and switching on makes the copy (see
        // ToggleRowAsync).
        var template = TemplateFor(group, model);
        var on = template?.AutoApply == true;
        var reach = template is null ? 0 : ContentPlacement.Reach(template, _packs, model.Overrides);

        var when = info.LastPlayed is { } played
            ? $"played {TimeFormat.DateTime(played)}"
            : $"changed {TimeFormat.DateTime(save.LastModified)}";
        // Name the copy the date belongs to when there are several.
        if (others.Count > 0) when += $" in {save.SourcePackName}";

        var tooltip = new List<string>
        {
            save.DisplayName,
            $"Folder: {save.FolderPath}",
            $"Instance: {save.SourcePackName}" + (others.Count > 0 ? "  (the copy this row opens)" : ""),
            $"Size: {FormatSize(save.SizeBytes)}",
            $"Mode: {info.GameModeLabel} · {info.DifficultyLabel}"
                + (info.Hardcore ? " · hardcore" : "") + (info.AllowCommands ? " · cheats on" : ""),
            $"Minecraft: {info.VersionLabel}",
            $"Seed: {info.SeedLabel}",
            info.LastPlayed is { } last
                ? $"Last played: {TimeFormat.LongDateTime(last)}"
                : $"Last modified: {TimeFormat.LongDateTime(save.LastModified)}"
        };
        if (others.Count > 0)
        {
            // Say "of this name" rather than "copies of this world": matching is by folder name only,
            // so two unrelated saves called New World share this row. Each copy is listed with its own
            // numbers.
            tooltip.Add($"{group.Copies.Count} instances have a save folder of this name. They are "
                      + $"{group.Copies.Count} separate saves with separate progress - a save is "
                      + "never shared:");
            foreach (var copy in group.Copies)
                tooltip.Add($"  · {copy.SourcePackName} - saves/{copy.FolderName}, "
                          + $"{FormatSize(copy.SizeBytes)}, "
                          + (copy.Info.LastPlayed is { } at
                              ? $"played {TimeFormat.DateTime(at)}"
                              : $"changed {TimeFormat.DateTime(copy.LastModified)}")
                          // Renaming changes LevelName and the launcher's label but not the folder, so a copy
                          // can have a different name and still group here.
                          + (copy.DisplayName == save.DisplayName ? "" : $", named '{copy.DisplayName}'"));
            tooltip.Add("They share one switch because the launcher holds one answer for that name - "
                      + "is this world offered to your instances. Everything that touches the files "
                      + "is per save and names which one: see the row's menu.");
        }
        if (running)
            tooltip.Add($"{JoinNames(busy)} {(busy.Count == 1 ? "is" : "are")} running - nothing may be "
                      + $"written inside {(busy.Count == 1 ? "that save" : "those saves")}.");

        var badges = new List<string> { info.GameType is null ? "world" : info.GameModeLabel.ToLowerInvariant() };
        if (info.Hardcore) badges.Add("hardcore");
        else if (info.AllowCommands) badges.Add("cheats");

        return new ContentRowState
        {
            Key = group.Key,
            DisplayName = save.DisplayName,
            Item = null,
            PackId = save.SourcePackId,
            Path = save.FolderPath,
            // Folder, location, size, with location and size widened when there are several saves of
            // this name (a count and a total rather than one size). The line is trimmed on narrow rows
            // (ContentListView), so the most useful parts come first; the count is also in the coverage
            // column, which never trims.
            MetaLine = $"saves/{save.FolderName}"
                     + $"  ·  in {(others.Count == 0 ? save.SourcePackName : JoinNames(group.Copies.Select(c => c.SourcePackName)))}"
                     + $"  ·  {(others.Count == 0 ? FormatSize(save.SizeBytes) : $"{group.Copies.Count} separate saves, {FormatSize(totalBytes)} in total")}"
                     + $"  ·  {string.Join(" · ", badges)}  ·  {when}"
                     + (info.VersionName is { Length: > 0 } v ? $"  ·  MC {v}" : ""),
            // No RulesLine: a world is either offered everywhere or not, and which instances it reaches
            // is listed on its own page.
            CoverageLine = others.Count == 0 ? "in 1 instance" : $"in {group.Copies.Count} instances",
            CoverageTip = others.Count == 0
                ? $"Only in {save.SourcePackName}."
                : $"A save of this name is also in: {string.Join(", ", others.Select(o => o.SourcePackName))}. Those are separate "
                  + "worlds with separate progress - a save is never shared.",
            Tooltip = string.Join("\n", tooltip),
            IsDefault = on,
            FolderLabel = folder ?? "",
            FolderTip = folder is null ? "" : $"Filed under {folder}",
            OffHere = running,
            OffHereTip = running
                ? $"{JoinNames(busy)} {(busy.Count == 1 ? "is" : "are")} running. Minecraft holds "
                  + $"{(busy.Count == 1 ? "this save" : "these saves")} open and rewrites "
                  + $"{(busy.Count == 1 ? "it" : "them")} continuously, so nothing on this page will "
                  + $"touch {(busy.Count == 1 ? "it" : "them")} until the game closes."
                : "",
            // The switch. Unlike the other pages, off doesn't undo on: two instances can't share a save
            // (Minecraft writes it continuously), so on copies the world into every compatible instance,
            // and each copy has its own progress from then on. Off only stops offering it and deletes
            // nothing; deleting a copy is on the row menu, which names each copy's instance. One switch
            // per world, since every copy resolves to the same library item and bit.
            ToggleOn = on,
            CanToggle = !running,
            ToggleTip = running
                ? $"{JoinNames(busy)} {(busy.Count == 1 ? "is" : "are")} running - close Minecraft first."
                : on
                    ? $"Stop offering {save.DisplayName} to your instances. THE COPIES ALREADY MADE "
                      + "STAY: each one is that instance's own world from the moment it was played, "
                      + "and deleting one is on this row's menu."
                      + (others.Count > 0
                          ? $" {group.Copies.Count} instances have a save of this name already, and "
                            + "this is the one switch all of them answer to."
                          : "")
                    : $"Copy {save.DisplayName} into every compatible instance. Each copy is a separate "
                      + "world from the moment it is played - nothing is kept in step, and nothing is "
                      + "ever copied back over your progress."
                      + (reach > 0 ? $" {reach} instance(s) would get one." : ""),
            CanOpen = true,
            Icon = WorldRow.LoadWorldIcon(save.FolderPath)
        };
    }

    /// <summary>
    /// The launcher's own copy of this world, whichever of its saves the library recognised.
    /// </summary>
    /// <remarks>Every copy's key is checked: with a scope picked the row speaks for that instance's
    /// copy, and the library item may only match one copy's spelling.</remarks>
    private static LibraryItem? TemplateFor(WorldGroup group, WorldModel model)
    {
        foreach (var key in group.SaveKeys)
            if (model.Templates.TryGetValue(key, out var item)) return item;
        return null;
    }

    /// <summary>"A", "A and B", "A, B and C", for use in a sentence.</summary>
    private static string JoinNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1]
        };
    }

    /// <summary>The user folder this world is filed under, or null.</summary>
    /// <remarks>Checks every copy's save key, not the row key, because folder membership is stored under
    /// save keys in settings.json (see <see cref="MemberComparer"/>). Uses the same comparer as the
    /// filter and the chip count so they agree.</remarks>
    private static string? FolderOf(WorldGroup group)
    {
        foreach (var (name, members) in App.State.Settings.WorldFolders)
            if (group.SaveKeys.Any(k => members.Any(m => MemberComparer.Equals(m, k)))) return name;
        return null;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
      : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
      : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
      : $"{bytes} B";

    // ── the scope box ────────────────────────────────────────────────────────

    /// <summary>Guarded wrapper. Builds the instance scope selector.</summary>
    /// <remarks>Runs inside the page refresh's try, whose catch reports that the saves folders couldn't
    /// be read. Guarded so a UI bug here doesn't blank the page as a data failure.</remarks>
    private void RebuildScopeBox()
    {
        try { RebuildScopeBoxCore(); }
        catch (Exception ex)
        {
            AppLog.LogError("worlds.scope-box", ex);
            // Leave whatever the box already had: a stale scope list still lets you pick.
        }
    }

    private void RebuildScopeBoxCore()
    {
        using (_filling.Hold())
        {
            var items = new List<WorldScopeItem> { new(null, "All instances") };
            items.AddRange(_packs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new WorldScopeItem(p.Id, p.Name)));

            var keep = items.FirstOrDefault(i => i.Id == _scopePackId) ?? items[0];
            _scopePackId = keep.Id;

            PackScopeBox.ItemsSource = items;
            PackScopeBox.DisplayMemberPath = nameof(WorldScopeItem.Label);
            PackScopeBox.SelectedItem = keep;
        }
        UpdateScopeDependentTips();
    }

    private void OnPackScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy) return;
        _scopePackId = (PackScopeBox.SelectedItem as WorldScopeItem)?.Id;
        UpdateScopeDependentTips();
        // The scope changes which saves are listed at all, so this is a rebuild rather than a filter
        // pass.
        _ = LoadAsync();
    }

    /// <summary>The "Open folder" tooltip names the instance it will open, since this page spans every
    /// instance.</summary>
    private void UpdateScopeDependentTips()
    {
        var pack = ScopedPack() ?? SoleInstance();
        OpenFolderButton.ToolTip = pack is null
            ? "Open an instance's saves folder - pick which one"
            : $"Open {pack.Name}'s saves folder";
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
        if (!Enum.TryParse<WorldSortMode>(tag, out var parsed)) return;
        _sortMode = parsed;
        App.State.Settings.WorldSortMode = parsed;
        App.State.Settings.Save();
        SyncSortChecks();
        ApplyFilter();
    }

    private void SyncSortChecks()
    {
        SortModifiedMenuItem.IsChecked = _sortMode == WorldSortMode.Modified;
        SortNameMenuItem.IsChecked = _sortMode == WorldSortMode.Name;
        SortSizeMenuItem.IsChecked = _sortMode == WorldSortMode.Size;
        SortPackMenuItem.IsChecked = _sortMode == WorldSortMode.Pack;

        // An icon-only button always announces the mode it is in.
        SortButton.ToolTip = "Sort worlds: " + _sortMode switch
        {
            WorldSortMode.Name => "Name",
            WorldSortMode.Size => "Size",
            WorldSortMode.Pack => "Instance",
            _ => "Last played"
        };
    }

    // ── folders ──────────────────────────────────────────────────────────────

    /// <summary>Guarded wrapper. Builds the folder chip strip.</summary>
    /// <remarks>Runs inside the page refresh's try, whose catch reports that the saves folders couldn't
    /// be read. Guarded so a UI bug here doesn't blank the page as a data failure.</remarks>
    private void RebuildFolders()
    {
        try { RebuildFoldersCore(); }
        catch (Exception ex)
        {
            AppLog.LogError("worlds.folder-chips", ex);
            _folderChips.Clear();   // an empty strip beats a half-built one
        }
    }

    /// <summary>
    /// Builds the chips: All, every user folder, and the trailing "New folder".
    /// </summary>
    /// <remarks>
    /// <para>Same strip as the Instances page (<c>PackListView.RefreshFolderChips</c>).</para>
    /// <para>A stale active folder (from settings synced from another machine) is reset here, or the
    /// list would be filtered by a chip that isn't shown.</para>
    /// <para><c>AppSettings.WorldFolderRules</c> is unused but kept so stored rules aren't lost.</para>
    /// </remarks>
    private void RebuildFoldersCore()
    {
        // Stored save key -> the row that speaks for it, via MemberComparer, so a chip's count
        // matches the rows it filters to (rows are grouped, stored keys are per save).
        var rowOf = RowOfSaveKey();
        var chips = new List<WorldFolderChipVm>
        {
            new("", "All", "", _all.Count, _activeFolder is null, "Every world on this page")
        };

        foreach (var name in App.State.Settings.WorldFolders.Keys
                     .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
        {
            var count = MemberCount(App.State.Settings.WorldFolders[name], rowOf);
            chips.Add(new WorldFolderChipVm(name, name, "", count, _activeFolder == name,
                $"Worlds you filed under {name}. Folders are a saved view - nothing moves on disk."));
        }

        // The trailing chip makes a folder, in the same last slot as on every other strip.
        chips.Add(new WorldFolderChipVm(NewFolderChipId, "New folder", NewFolderGlyph, 0, false,
            "Create a folder to file worlds under"));

        // Diffed rather than cleared: a Reset on this collection re-runs the chips' entrance
        // animation on every keystroke in the search box.
        ListDiff.Apply(_folderChips, chips, c => c.Id, (kept, fresh) => kept.CopyFrom(fresh));

        if (_activeFolder is { Length: > 0 } active
            && !App.State.Settings.WorldFolders.ContainsKey(active))
        {
            _activeFolder = null;
            foreach (var chip in _folderChips) chip.SetActive(chip.Id.Length == 0);
        }
    }

    /// <summary>The reserved-name rule, applied on create and on rename.</summary>
    /// <remarks>A leading colon marks the strip's pseudo-folders and <c>team:</c> marks a computed team
    /// folder in <see cref="PackListView"/>; a user folder spelled either way would be mistaken for one
    /// of those.</remarks>
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
        if (App.State.Settings.WorldFolders.ContainsKey(name))
        {
            Fail("A folder with that name already exists.");
            return;
        }

        App.State.Settings.CreateWorldFolder(name);
        // Through the row helper, so a world in several instances is filed as a whole, not just
        // the copy the row speaks for.
        if (addThis is not null) SetMemberRow(name, addThis, true);
        App.State.Settings.Save();

        _activeFolder = name;
        RebuildFolders();
        ApplyFilter();
        Okay(addThis is null
            ? $"Created the folder '{name}'. An import while it is the active chip is filed into it."
            : $"Created the folder '{name}' and filed {addThis.DisplayName} in it.");
    }

    private void OnFolderChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorldFolderChipVm chip }) return;
        if (chip.Id == NewFolderChipId) { _ = CreateFolderAsync(null); return; }
        _activeFolder = string.IsNullOrEmpty(chip.Id) ? null : chip.Id;
        RebuildFolders();
        ApplyFilter();
    }

    /// <summary>All and New folder are computed views, so they can't be renamed.</summary>
    private void OnFolderChipRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorldFolderChipVm chip } && !chip.IsUserFolder)
            e.Handled = true;
    }

    private static WorldFolderChipVm? FolderFromMenu(object src)
    {
        if (src is not MenuItem leaf) return null;
        DependencyObject? p = leaf;
        while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
        return p is ContextMenu { PlacementTarget: FrameworkElement fx } && fx.DataContext is WorldFolderChipVm row
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

            // Renames both maps (members and rules) together and keeps the folder's position in the
            // strip, so the folder's rule isn't left behind under the old name.
            if (!App.State.Settings.RenameWorldFolder(chip.Id, fresh))
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

            var count = App.State.Settings.WorldFolders.TryGetValue(chip.Id, out var members)
                ? members.Count : 0;
            if (!await AppDialog.ConfirmAsync(_shell, "Delete folder",
                    $"Delete the folder '{chip.Id}'?"
                    + (count > 0
                        ? $"\n\nThe {count} world(s) filed in it stay exactly where they are - only the "
                          + "grouping goes."
                        : ""),
                    "Delete folder", "Cancel", danger: true))
                return;

            App.State.Settings.DeleteWorldFolder(chip.Id);
            if (_activeFolder == chip.Id) _activeFolder = null;
            App.State.Settings.Save();

            RebuildFolders();
            ApplyFilter();
            Okay($"Deleted the folder '{chip.Id}'.");
        }
        catch (Exception ex) { Fail("That folder could not be deleted.", ex); }
    }

    /// <summary>Takes a deleted save's key out of every folder, so a folder cannot keep counting
    /// something that is gone.</summary>
    private static void ForgetFolderMembership(string key)
    {
        foreach (var members in App.State.Settings.WorldFolders.Values)
            members.RemoveAll(k => MemberComparer.Equals(k, key));
    }

    // ── folder membership: one comparer, one answer ──────────────────────────

    /// <summary>
    /// The comparer for every "is this filed under that folder" question, so the filter, the chip
    /// count and <see cref="ContentFolderRules"/> agree.
    /// </summary>
    /// <remarks>OrdinalIgnoreCase like the rule engine, because keys are save-folder names, which are
    /// case-insensitive on Windows. <c>AppSettings.AddWorldToFolder</c> de-dups ordinally, so older
    /// settings can hold case-only duplicates; <see cref="SetMember"/> removes every match and
    /// <see cref="MemberCount"/> counts them once.</remarks>
    private static readonly StringComparer MemberComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>The user folder the strip is filtered to, or null for All.</summary>
    private string? ActiveUserFolder() =>
        _activeFolder is { Length: > 0 } folder
        && !IsReservedFolderName(folder)
        && App.State.Settings.WorldFolders.ContainsKey(folder)
            ? folder
            : null;

    /// <summary>One folder's member keys: the stored list itself, not a copy.</summary>
    private static List<string>? MemberKeys(string? folder) =>
        folder is { Length: > 0 }
        && App.State.Settings.WorldFolders.TryGetValue(folder, out var list)
            ? list
            : null;

    /// <summary>Is this key filed under that folder?</summary>
    private static bool IsMember(IReadOnlyList<string>? members, string key) =>
        members is not null && members.Any(k => MemberComparer.Equals(k, key));

    /// <summary>Is this row filed under that folder, i.e. is any of its copies?</summary>
    /// <remarks>Membership is stored as a save key per copy, and older settings may have only some copies
    /// filed, so any copy counts. <see cref="SetMemberRow"/> writes every copy.</remarks>
    private bool IsMemberRow(IReadOnlyList<string>? members, ContentRow row) =>
        members is not null
        && GroupOf(row) is { } group
        && group.SaveKeys.Any(k => IsMember(members, k));

    /// <summary>Files every copy of this world under a folder, or takes them all out, and saves.</summary>
    /// <remarks>Every copy, because the row is the whole world. Falls back to the row's own key for a row
    /// the model no longer knows; no other row uses that key, so nothing is filed wrongly.</remarks>
    private void SetMemberRow(string folder, ContentRow row, bool wanted)
    {
        var keys = GroupOf(row) is { } group ? group.SaveKeys.ToList() : [row.Key];
        foreach (var key in keys) SetMember(folder, key, wanted);
    }

    /// <summary>Files a key under a folder, or takes it out, and saves.</summary>
    /// <remarks>Saves immediately, unlike the <see cref="AppSettings"/> mutators it calls, since filing is
    /// a single menu click with no dialog to confirm it.</remarks>
    private static void SetMember(string folder, string key, bool wanted)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(key)) return;
        if (wanted)
        {
            if (!IsMember(MemberKeys(folder), key)) App.State.Settings.AddWorldToFolder(folder, key);
        }
        else if (MemberKeys(folder) is { } list)
        {
            // Remove every match, not just the first (see MemberComparer).
            list.RemoveAll(k => MemberComparer.Equals(k, key));
        }
        App.State.Settings.Save();
    }

    /// <summary>The number on a folder's chip: how many of its members this page has a row for.</summary>
    /// <remarks>Counted in rows, not stored keys: <paramref name="rowOf"/> maps each stored save key to
    /// its row and distinct rows are counted, so a world filed as three copies counts once, as in the
    /// list.</remarks>
    private static int MemberCount(IReadOnlyList<string> members, IReadOnlyDictionary<string, string> rowOf)
    {
        var rows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
            if (rowOf.TryGetValue(member, out var key)) rows.Add(key);
        return rows.Count;
    }

    /// <summary>Stored save key -> the row that speaks for it, for every row on the page.</summary>
    /// <remarks>Built with <see cref="MemberComparer"/> so keys stored with a different case still find
    /// their row.</remarks>
    private Dictionary<string, string> RowOfSaveKey()
    {
        var map = new Dictionary<string, string>(MemberComparer);
        foreach (var row in _all)
            if (GroupOf(row) is { } group)
                foreach (var key in group.SaveKeys) map[key] = row.Key;
        return map;
    }

    /// <summary>
    /// Files this world (every copy of it) under a folder, or takes it out.
    /// </summary>
    /// <remarks>Only writes a list in settings.json; no files move, and the status message says so. No
    /// reload is needed: <see cref="ApplyFilter"/> updates the list and the chip counts.</remarks>
    private void FileIntoFolder(ContentRow row, string folder, bool wanted)
    {
        try
        {
            SetMemberRow(folder, row, wanted);
            ApplyFilter();
            Okay(wanted
                ? $"{row.DisplayName} is filed under {folder}. Nothing moved on disk - a folder is a "
                  + "saved view."
                : $"{row.DisplayName} is no longer filed under {folder}.");
        }
        catch (Exception ex) { Fail("That folder could not be changed.", ex); }
    }

    // ── filtering ────────────────────────────────────────────────────────────

    /// <summary>
    /// Narrows <see cref="_all"/> into the list the user sees, and writes the page's count.
    /// </summary>
    /// <remarks>Diffed into the bound collection (see <see cref="ListDiff"/>). Assigning a new list would
    /// reset the scroll position, the selection and the entrance animation on every keystroke.</remarks>
    private void ApplyFilter(bool announce = false)
    {
        RebuildFolders();

        IEnumerable<ContentRow> seq = _all;
        var filtered = false;

        // The folder chip is a plain filter, as on the Instances page. It checks the row's save
        // keys because that is what is stored, and a world may be filed under only one copy's key.
        var folder = ActiveUserFolder();
        if (MemberKeys(folder) is { } members)
        {
            var set = new HashSet<string>(members, MemberComparer);
            seq = seq.Where(r => GroupOf(r) is { } g
                ? g.SaveKeys.Any(set.Contains)
                : set.Contains(r.Key));
            filtered = true;
        }

        var query = SearchBox.Text.Trim();
        if (query.Length > 0)
        {
            filtered = true;
            seq = seq.Where(r => Matches(r, query));
        }

        ListDiff.Apply(WorldList.Rows, Sorted(seq).ToList(), r => r.Key);

        if (_scopePackId is not null) filtered = true;

        // Only the load path decides the page's state; a keystroke while a scan is running
        // must not overwrite "Scanning..." with a count from a half-filled list.
        if (!announce && _state.Kind is PageStateKind.Loading or PageStateKind.Refreshing) return;

        if (WorldList.Rows.Count == 0 && filtered && _all.Count > 0)
        {
            // Tell the empty panel whether the folder is empty or the search matched nothing, since
            // the user acts on those differently.
            if (folder is { Length: > 0 } active && query.Length == 0)
                _state.EmptyNext($"Nothing in {active} yet",
                    "Right-click a world on the All chip and pick 'Add to folder' to file it here. "
                    + "Nothing moves on disk - a folder is a saved view - and an import while this chip "
                    + "is active is filed here automatically.",
                    glyph: "");
            else
                _state.EmptyFiltered();
        }

        _state.Content(WorldList.Rows.Count, note: _cacheNote ?? _staleNote ?? LibraryOnlyNote());
    }

    private WorldRowFacts FactsFor(ContentRow row) =>
        _model.Facts.TryGetValue(row.Key, out var facts)
            ? facts
            : new WorldRowFacts(0, DateTimeOffset.MinValue, "");

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
            WorldSortMode.Name =>
                rows.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            WorldSortMode.Size =>
                rows.OrderByDescending(r => FactsFor(r).SizeBytes)
                    .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            // Sort by the instance of the row's own copy: the scoped one, or else the most
            // recently played copy.
            WorldSortMode.Pack =>
                rows.OrderBy(r => FactsFor(r).Instance, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ =>
                rows.OrderByDescending(r => FactsFor(r).Modified)
                    .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };

    // ── header actions ───────────────────────────────────────────────────────

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await LoadAsync(force: true); }
        catch (Exception ex) { Fail("The saves folders could not be re-read.", ex); }
    }

    private void OnBrowse(object sender, RoutedEventArgs e) => _shell.OpenWorldBrowser();

    /// <summary>The data pack store. Data packs live inside worlds, so this page is where the store
    /// is found; the worlds are picked after a download.</summary>
    /// <remarks>The instance in scope starts selected and sets the Minecraft version filter. Each
    /// world's own page lists its packs and opens the same store with that world ticked.</remarks>
    private void OnBrowseDataPacks(object sender, RoutedEventArgs e) =>
        _shell.OpenDataPackBrowser(_packs, _scopePackId, preferredWorldDir: null, pushed: false);

    /// <summary>
    /// The Import flow: pick the .zip or the save folder, pick the instances (or none), publish it if
    /// you want.
    /// </summary>
    /// <remarks>The flow lives in <see cref="ImportContentCard"/>, shared by all four content pages. A
    /// world imported with no instance ticked only lands in the library and gets no row here;
    /// <see cref="LibraryOnlyNote"/> mentions it.</remarks>
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try { await RunImportAsync(null); }
        catch (Exception ex) { Fail("That import did not finish.", ex); }
    }

    private async Task RunImportAsync(string? prePickedPath)
    {
        // Snapshot first: the card reports which instances were written to, not the save folder a
        // world became, and the active folder needs the new rows' keys.
        var before = _all.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);

        var outcome = await ImportContentCard.ShowAsync(_shell, new ImportRequest(
            ImportContentKind.World, _packs,
            PreferredPackId: ScopedPack()?.Id ?? SoleInstance()?.Id,
            PrePickedPath: prePickedPath));
        if (outcome is null) return;

        // A folder stamp can miss our own write, so drop the remembered scan for every instance
        // we touched first.
        foreach (var target in outcome.Targets) ScanCaches.InvalidatePack(target, ScanScope.Worlds);
        await LoadAsync(force: true);

        var filed = FileNewRowsIntoActiveFolder(before);
        Okay(filed == 0 || _activeFolder is null
            ? outcome.Summary
            : $"{outcome.Summary}  ·  filed into {_activeFolder}");
    }

    /// <summary>
    /// Files whatever the import added into the active folder chip.
    /// </summary>
    /// <remarks>An import while a folder chip is active goes into that folder. The keys are diffed
    /// because the import's result reports instances, not the save folder a world became.</remarks>
    private int FileNewRowsIntoActiveFolder(HashSet<string> before)
    {
        if (ActiveUserFolder() is not { } folder) return 0;

        var added = 0;
        foreach (var row in _all)
        {
            if (before.Contains(row.Key)) continue;
            // Every copy of the new world, through the row helper, so an import into several
            // instances is filed as one row.
            SetMemberRow(folder, row, true);
            added++;
        }
        if (added > 0)
        {
            App.State.Settings.Save();
            RebuildFolders();
            ApplyFilter();
        }
        return added;
    }



    /// <summary>
    /// Opens a saves folder in Explorer.
    /// </summary>
    /// <remarks>Every instance has its own saves folder, so this picks one: the scoped instance, the
    /// only instance, or a short menu.</remarks>
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var pack = ScopedPack() ?? SoleInstance();
        if (pack is not null) { OpenSaves(pack); return; }
        if (_packs.Count == 0)
        {
            Fail("There is no instance to open yet.");
            return;
        }

        var menu = new ContextMenu { PlacementTarget = OpenFolderButton, Placement = PlacementMode.Bottom };
        foreach (var candidate in _packs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var target = candidate;
            menu.Items.Add(ContentMenu.Item($"Saves in {target.Name}", () => OpenSaves(target)));
        }
        menu.IsOpen = true;
    }

    private void OpenSaves(PackSummary pack)
    {
        try
        {
            // Create-then-open, so it never fails on an instance that has not been launched yet.
            var dir = App.State.Worlds.SavesDir(pack.Id, pack.Name);
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) Fail("That folder could not be opened.");
        }
        catch (Exception ex) { Fail("That folder could not be opened.", ex); }
    }

    // ── row callbacks ────────────────────────────────────────────────────────

    /// <summary>Clicking a row opens the world's page in the right-hand pane.</summary>
    private void OpenRow(ContentRow row)
    {
        if (SaveOf(row) is { } save) _shell.OpenWorldDetail(save.Key, save.DisplayName);
    }

    /// <summary>The world behind a row (every copy of it), looked up in the current model so an
    /// action never uses a path the latest scan no longer found.</summary>
    private WorldGroup? GroupOf(ContentRow row) => _model.Groups.GetValueOrDefault(row.Key);

    /// <summary>How many saves of this one's folder name the last scan found, across every instance.</summary>
    /// <remarks>Status messages name the instance once there is more than one copy; otherwise
    /// "Renamed to X" looks like it didn't apply, since the row still lists the others.</remarks>
    private int CopyCountOf(WorldInfo save) =>
        _model.Groups.GetValueOrDefault(WorldKeyOf(save.FolderName))?.Copies.Count ?? 1;

    /// <summary>The save a row acts on by default: the copy it speaks for.</summary>
    /// <remarks>The most recently played copy, or the scoped instance's. Actions that write to a save and
    /// could mean several copies don't use this; they ask by name on the row menu (see
    /// <see cref="SaveMenu"/>).</remarks>
    private WorldInfo? SaveOf(ContentRow row) => GroupOf(row)?.Primary;

    /// <summary>
    /// The row's switch: whether this world is copied into every compatible instance.
    /// </summary>
    /// <remarks>A save the launcher has no copy of is copied into the library first. See
    /// <see cref="SetOfferedAsync"/> for what on and off do.</remarks>
    private async Task ToggleRowAsync(ContentRow row, bool wanted)
    {
        try
        {
            if (GroupOf(row) is not { } group)
            {
                Fail("That save is no longer where it was - try Refresh.");
                return;
            }

            // Check every instance the world is in: switching on writes a copy to all of them, and
            // nothing may be written while the game is running.
            foreach (var copy in group.Copies)
                if (InUse(copy.SourcePackId, "changing what this world is offered to")) return;

            if (TemplateFor(group, _model) is { } known)
            {
                await SetOfferedAsync(known, row.DisplayName, wanted);
                return;
            }

            // Nothing to stop offering: without a library copy it was never handed out. Happens when
            // the row was rebuilt during the click.
            if (!wanted) return;
            if (await PromoteAsync(group.Primary) is not { } item) return;
            await SetOfferedAsync(item, row.DisplayName, true);
        }
        catch (Exception ex) { Fail("That world could not be switched.", ex); }
    }

    /// <summary>
    /// Gives the launcher its own copy of a save so it has something to hand out. Returns the item,
    /// or null when nothing could be copied.
    /// </summary>
    /// <remarks>A full copy, which can be gigabytes, so it runs under the status bar's Cancel.</remarks>
    private async Task<LibraryItem?> PromoteAsync(WorldInfo save)
    {
        // Say "copying" and why, since turning this on uses as much disk as the save takes.
        var ct = BeginBusy($"Turning on {save.DisplayName} - copying it, because a save is never "
                         + "shared between instances...");
        try
        {
            var result = await App.State.ContentDefaults.AddFromInstanceAsync(
                _packs.First(p => p.Id == save.SourcePackId),
                [new ContentAddRequest(save.FolderPath, LibraryKind.World, save.DisplayName)],
                makeDefault: false, policyTemplate: null,
                new Progress<string>(_state.Progress), ct);

            EndBusy();
            await LoadAsync(force: true);
            // Refused outright (the game is running in that instance) is a failure, since nothing
            // was attempted.
            if (result.Refused is { Length: > 0 } refused) { Fail(refused); return null; }
            return result.Items.FirstOrDefault()?.Item;
        }
        catch (OperationCanceledException) { Okay("Copy cancelled."); return null; }
        catch (Exception ex) { Fail("That world could not be copied.", ex); return null; }
        finally { EndBusy(); }
    }

    /// <summary>
    /// Starts or stops offering this world to every compatible instance.
    /// </summary>
    /// <remarks>On copies the world into every compatible instance, and each copy becomes that
    /// instance's own world once played. Off only stops offering it and deletes nothing, since removing
    /// the copies could delete someone's progress; deleting a copy is on the row menu. The bit is
    /// <c>AutoApply</c> on the library item, shared by every copy of the save.</remarks>
    private async Task SetOfferedAsync(LibraryItem item, string displayName, bool wanted)
    {
        try
        {
            var policy = App.State.Library.GetDefaults(item.Key);
            policy.Enabled = wanted;
            // A world has no in-game "on" (it's just a folder in saves/), so Activate is left alone,
            // unlike on the pack pages.

            // Anything the launcher places is kept on this machine unless the user says otherwise.
            // saves/ isn't synced, but the flag travels with the item.
            var keepLocal = policy.KeepLocal || wanted;
            policy.KeepLocal = keepLocal;
            App.State.Library.SetDefaults(item.Key, policy);
            if (keepLocal) App.State.Library.SetKeepLocal(item.Key, true);

            await LoadAsync(force: true);
            Okay(wanted
                ? $"{displayName} goes into every compatible instance. Each copy is a separate world "
                  + "from the moment it is played."
                : $"{displayName} is no longer offered. The copies already in your instances stay "
                  + "exactly where they are - each one is that instance's own world now.");
        }
        catch (Exception ex) { Fail("That world could not be switched.", ex); }
    }

    // ── the row menu ─────────────────────────────────────────────────────────

    private const int IcOpen = 0xE8A5, IcPlay = 0xE768, IcCopy = 0xE8C8, IcExport = 0xEDE1;
    private const int IcBackup = 0xE8F7, IcRename = 0xE8AC, IcReveal = 0xE8DA, IcFolder = 0xE8F1;
    private const int IcDelete = 0xE74D, IcMore = 0xE712;

    /// <summary>
    /// The row's menu, built when it is opened: the short list on left-click of the "..." button,
    /// everything on right-click.
    /// </summary>
    /// <remarks>Built on demand so rows don't each carry a large menu. For a world in several instances,
    /// items that read, write or open a save become submenus naming the instances
    /// (<see cref="PerCopy"/>); only the switch and folder filing apply to the whole world.</remarks>
    private ContextMenu SaveMenu(ContentRow row, bool full)
    {
        var group = GroupOf(row);
        var save = group?.Primary;
        var menu = ContentMenu.New(row.DisplayName,
            group is null || save is null ? "a save that is no longer there"
            : group.Copies.Count == 1 ? $"save in {save.SourcePackName}"
            : $"{group.Copies.Count} separate saves of this name - in "
              + JoinNames(group.Copies.Select(c => c.SourcePackName)));
        if (group is null || save is null) return menu;

        menu.Items.Add(BuildPlayMenu(group));
        menu.Items.Add(PerCopy(group, "Open world", IcOpen,
            copy => _shell.OpenWorldDetail(copy.Key, copy.DisplayName)));
        // "Add to folder" is in both the short and the long menu, as on the Instances page, since
        // filing is the main thing this page does besides opening a world. It applies to the whole
        // world, so it's asked once.
        menu.Items.Add(BuildFolderMenu(row));

        if (!full)
        {
            menu.Items.Add(PerCopy(group, "Reveal in Explorer", IcReveal, copy => Reveal(copy.FolderPath)));
            menu.Items.Add(new Separator());
            menu.Items.Add(ContentMenu.Item("More options...",
                () => ContentMenu.ShowInstead(menu, () => SaveMenu(row, full: true)),
                ContentMenu.Glyph(IcMore)));
            return menu;
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(PerCopy(group, "Rename...", IcRename, copy => _ = RenameSaveAsync(copy),
            gesture: "F2"));
        menu.Items.Add(PerCopy(group, "Duplicate", IcCopy, copy => _ = DuplicateAsync(copy)));
        menu.Items.Add(PerCopy(group, "Copy to instance...", IcCopy, copy => _ = CopyToInstanceAsync(copy)));
        menu.Items.Add(PerCopy(group, "Export as .zip...", IcExport, copy => _ = ExportZipAsync(copy)));
        menu.Items.Add(PerCopy(group, "Back up now", IcBackup, copy => _ = BackupAsync(copy)));
        menu.Items.Add(PerCopy(group, "Reveal in Explorer", IcReveal, copy => Reveal(copy.FolderPath)));

        menu.Items.Add(new Separator());
        // Not per copy: every copy has the same seed.
        menu.Items.Add(ContentMenu.Item("Copy seed", () =>
        {
            if (save.Info.Seed is not { } seed)
            {
                Fail("This world's level.dat does not record a seed.");
                return;
            }
            Okay(ClipboardHelper.TrySetText(seed.ToString())
                ? $"Seed {seed} copied."
                : "The clipboard is busy - try again.");
        }, ContentMenu.Glyph(IcCopy), enabled: save.Info.HasSeed,
            gesture: save.Info.HasSeed ? null : "not recorded"));
        // Per copy, unlike the seed: a world key names one save in one instance.
        menu.Items.Add(PerCopy(group, "Copy world key", 0, copy =>
        {
            if (ClipboardHelper.TrySetText(copy.Key)) Okay("World key copied.");
        }));

        menu.Items.Add(new Separator());
        // The only destructive item. With several copies it becomes a submenu with one delete
        // per instance, each showing that copy's size. It always deletes a single save.
        menu.Items.Add(PerCopy(group, "Delete...", IcDelete, copy => _ = DeleteSaveAsync(copy),
            gesture: "Del", itemLabel: copy => $"Delete from {copy.SourcePackName}..."));
        return menu;
    }

    /// <summary>
    /// One menu item for a world that is in one instance; a submenu naming the instances when it is
    /// in more than one.
    /// </summary>
    /// <remarks>Each copy's size is shown in the gesture column to help tell them apart. The keyboard
    /// gesture only shows on the single item, because F2 and Del refuse a multi-copy row.</remarks>
    private static MenuItem PerCopy(WorldGroup group, string label, int glyph, Action<WorldInfo> act,
                                    string? gesture = null, Func<WorldInfo, string>? itemLabel = null)
    {
        if (group.Copies.Count == 1)
        {
            var only = group.Copies[0];
            return ContentMenu.Item(itemLabel?.Invoke(only) ?? label, () => act(only),
                                    ContentMenu.Glyph(glyph), gesture: gesture);
        }

        var parent = ContentMenu.Parent(label, ContentMenu.Glyph(glyph));
        foreach (var candidate in group.Copies)
        {
            var copy = candidate;
            parent.Items.Add(ContentMenu.Item(itemLabel?.Invoke(copy) ?? copy.SourcePackName,
                                              () => act(copy),
                                              gesture: FormatSize(copy.SizeBytes)));
        }
        return parent;
    }

    private MenuItem BuildFolderMenu(ContentRow row)
    {
        var parent = ContentMenu.Parent("Add to folder", ContentMenu.Glyph(IcFolder));
        parent.SubmenuOpened += (_, _) =>
        {
            parent.Items.Clear();
            foreach (var name in App.State.Settings.WorldFolders.Keys
                         .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
            {
                var folder = name;
                // Through the same helper as the chip's count, so the menu and the count agree
                // (case-insensitive).
                var inside = IsMemberRow(MemberKeys(folder), row);
                parent.Items.Add(ContentMenu.Toggle(folder, inside, () =>
                {
                    FileIntoFolder(row, folder, !inside);
                    return !inside;
                }));
            }
            // Same items and wording as the Instances page's "Add to folder"
            // (PackListView.OnCtxAddToFolderOpened), so filing works the same on both pages.
            if (App.State.Settings.WorldFolders.Count == 0)
                parent.Items.Add(ContentMenu.Item("(no folders - create one first)", () => { },
                    enabled: false));

            parent.Items.Add(new Separator());
            parent.Items.Add(ContentMenu.Item("Create new folder...", () => _ = CreateFolderAsync(row)));
        };
        parent.Items.Add(ContentMenu.Item("(reading...)", () => { }, enabled: false));
        return parent;
    }

    // ── save actions ─────────────────────────────────────────────────────────

    /// <summary>
    /// "Play with...", as a submenu of the row's own menu.
    /// </summary>
    /// <remarks>The instances come from the legacy per-save compatibility list. An instance that has its
    /// own copy plays that copy; passing the row's copy to <see cref="PlayWithAsync"/> would ask whether
    /// to overwrite it, risking that instance's progress.</remarks>
    private MenuItem BuildPlayMenu(WorldGroup group)
    {
        var parent = ContentMenu.Parent("Play with...", ContentMenu.Glyph(IcPlay));
        parent.SubmenuOpened += (_, _) =>
        {
            parent.Items.Clear();
            var compatible = App.State.Worlds.CompatiblePacks(group.Primary, _packs);
            if (compatible.Count == 0)
            {
                parent.Items.Add(ContentMenu.Item(
                    "(no compatible instance - copy it into one from its own page)",
                    () => { }, enabled: false));
                return;
            }
            foreach (var candidate in compatible)
            {
                var pack = candidate;
                var save = group.In(pack.Id) ?? group.Primary;
                parent.Items.Add(ContentMenu.Item(pack.Name, () => _ = PlayWithAsync(save, pack.Id),
                    gesture: group.Copies.Count > 1 && group.In(pack.Id) is not null
                        ? "its own copy"
                        : null));
            }
        };
        parent.Items.Add(ContentMenu.Item("(reading...)", () => { }, enabled: false));
        return parent;
    }

    /// <summary>
    /// Launches an instance with this world, copying the save into that instance first if it isn't
    /// already there. If the target has its own copy, the user picks which one to play.
    /// </summary>
    private async Task PlayWithAsync(WorldInfo save, Guid packId)
    {
        try
        {
            if (App.State.MinecraftAccounts.Current is null)
            {
                _shell.OpenMcAccount();
                Fail("Sign in to a Minecraft account first.");
                return;
            }

            _state.Note("Launching...");
            var pack = await App.State.Api.GetPackAsync(packId);
            App.State.Packs.EnsurePackFolder(pack.Id, pack.Name, pack.IsShared);

            if (packId != save.SourcePackId)
            {
                var targetSaves = Path.Combine(App.State.Packs.GameDir(pack.Id), "saves", save.FolderName);
                if (!Directory.Exists(targetSaves))
                {
                    if (InUse(pack.Id, "copying a save into it")) return;
                    var ct = BeginBusy($"Copying the save into {pack.Name}...");
                    try
                    {
                        await WorldService.CopyWorldAsync(save.FolderPath, targetSaves,
                            Percent($"Copying into {pack.Name}"), ct);
                    }
                    finally { EndBusy(); }
                    ScanCaches.InvalidatePack(pack.Id, ScanScope.Worlds);
                }
                else if (await ConfirmReplaceExistingCopyAsync(save, pack.Name, targetSaves))
                {
                    if (InUse(pack.Id, "replacing a save inside it")) return;
                    var ct = BeginBusy($"Replacing the copy in {pack.Name}...");
                    try
                    {
                        // Not delete-then-copy: this token is the Cancel button's, and
                        // cancelling mid-copy would leave neither world. The swap only
                        // happens once the new copy is complete.
                        await WorldService.ReplaceWorldAsync(save.FolderPath, targetSaves,
                            Percent($"Replacing in {pack.Name}"), ct);
                    }
                    finally { EndBusy(); }
                    ScanCaches.InvalidatePack(pack.Id, ScanScope.Worlds);
                }
            }

            var proc = await App.State.Launcher.LaunchTrackedAsync(pack, new Progress<string>(_ => { }));
            _shell.OpenMinecraftHost(pack, proc);
            Okay($"Launched {pack.Name}.");
        }
        catch (OperationCanceledException) { Okay("Cancelled."); }
        catch (Exception ex) { Fail("That instance could not be launched.", ex); }
    }

    /// <summary>
    /// True to overwrite the copy already sitting in the target instance, false to play it as it is.
    /// </summary>
    /// <remarks>Replacing is the affirmative answer, so dismissing the dialog plays the existing copy,
    /// which can't lose progress. Both timestamps are shown since the folder names don't tell the copies
    /// apart.</remarks>
    private async Task<bool> ConfirmReplaceExistingCopyAsync(WorldInfo save, string packName, string existingPath)
    {
        var mine = WorldService.ReadMeta(save.FolderPath).LastPlayed ?? save.LastModified;
        var theirs = WorldService.ReadMeta(existingPath).LastPlayed
                     ?? (DateTimeOffset)Directory.GetLastWriteTime(existingPath);

        return await AppDialog.ConfirmAsync(_shell,
            $"{save.DisplayName} is already in {packName}",
            "That instance already has a copy of this save.\n\n"
            + $"Copy in {packName}: last played {TimeFormat.DateTime(theirs)}\n"
            + $"This save in {save.SourcePackName}: last played {TimeFormat.DateTime(mine)}\n\n"
            + $"Replacing overwrites the copy in {packName} and everything done in it.",
            "Replace it with this save", $"Play the copy in {packName}", danger: true);
    }

    /// <summary>
    /// Renames a world: the launcher label and the <c>LevelName</c> Minecraft shows.
    /// </summary>
    private async Task RenameSaveAsync(WorldInfo save)
    {
        try
        {
            if (InUse(save.SourcePackId, "renaming this world")) return;

            var name = (await _shell.PromptAsync("Rename world", "New name", save.DisplayName))?.Trim();
            if (string.IsNullOrEmpty(name) || name == save.DisplayName) return;

            await Task.Run(() => WorldService.SetLevelName(save, name));
            App.State.Worlds.Rename(save.Key, name);
            ScanCaches.InvalidatePack(save.SourcePackId, ScanScope.Worlds);
            await LoadAsync(force: true);
            // Name the copy when there are several: a rename doesn't change the folder, so the row
            // still groups it with the others and may still show another copy's name.
            Okay(CopyCountOf(save) > 1
                ? $"Renamed the save in {save.SourcePackName} to {name}. The other instances' saves of "
                  + "that folder name are untouched, and the row still lists them together."
                : $"Renamed to {name}.");
        }
        catch (IOException)
        {
            // Only the in-game name write failed; the launcher label was not changed either.
            Fail("Close Minecraft before renaming this world - its level.dat is in use.");
        }
        catch (Exception ex) { Fail("That world could not be renamed.", ex); }
    }

    private async Task DuplicateAsync(WorldInfo save)
    {
        if (InUse(save.SourcePackId, "duplicating a world in it")) return;
        var ct = BeginBusy($"Duplicating {save.DisplayName}...");
        try
        {
            var folder = await App.State.Worlds.DuplicateAsync(save, Percent("Duplicating"), ct);
            EndBusy();
            ScanCaches.InvalidatePack(save.SourcePackId, ScanScope.Worlds);
            await LoadAsync(force: true);
            Okay($"Duplicated as saves/{folder}.");
        }
        catch (OperationCanceledException) { Okay("Duplicate cancelled."); }
        catch (Exception ex) { Fail("That world could not be duplicated.", ex); }
        finally { EndBusy(); }
    }

    private async Task CopyToInstanceAsync(WorldInfo save)
    {
        try
        {
            var targets = _packs.Where(p => p.Id != save.SourcePackId).ToList();
            if (targets.Count == 0)
            {
                Fail("There is no other instance to copy this world into.");
                return;
            }

            var picker = new PackPickerDialog(targets, "Copy world to...",
                $"'{save.DisplayName}' will be copied into this instance's saves folder. The two copies are "
                + "separate worlds from then on.",
                "Copy") { Owner = _shell };
            if (picker.ShowDialog() != true || picker.SelectedPackId is not Guid packId) return;

            var pack = targets.First(p => p.Id == packId);
            if (InUse(pack.Id, "copying a world into it")) return;

            var ct = BeginBusy($"Copying {save.DisplayName} into {pack.Name}...");
            try
            {
                var folder = await App.State.Worlds.CopyToPackAsync(save, pack.Id, pack.Name,
                    Percent($"Copying into {pack.Name}"), ct);
                EndBusy();
                ScanCaches.InvalidatePack(pack.Id, ScanScope.Worlds);
                await LoadAsync(force: true);
                Okay($"Copied to {pack.Name} as saves/{folder}.");
            }
            finally { EndBusy(); }
        }
        catch (OperationCanceledException) { Okay("Copy cancelled."); }
        catch (Exception ex) { Fail("That world could not be copied.", ex); }
    }

    private async Task ExportZipAsync(WorldInfo save)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export world as .zip",
                Filter = "Zip archive (*.zip)|*.zip",
                FileName = WorldService.SafeFolderName(save.DisplayName) + ".zip"
            };
            if (dialog.ShowDialog(_shell) != true) return;

            // Export and backup only read the save and skip the lock file, so they're safe while
            // the game is running and aren't gated behind the in-use check.
            var ct = BeginBusy($"Zipping {save.DisplayName}...");
            try
            {
                await WorldService.ExportZipAsync(save, dialog.FileName, Percent("Zipping"), ct);
                Okay($"Exported to {dialog.FileName}.");
            }
            finally { EndBusy(); }
        }
        catch (OperationCanceledException) { Okay("Export cancelled."); }
        catch (Exception ex) { Fail("That world could not be exported.", ex); }
    }

    private async Task BackupAsync(WorldInfo save)
    {
        var ct = BeginBusy($"Backing up {save.DisplayName}...");
        try
        {
            var zip = await App.State.Worlds.BackupAsync(save, Percent("Backing up"), ct);
            Okay($"Backed up to {Path.GetFileName(zip)}.");
        }
        catch (OperationCanceledException) { Okay("Backup cancelled."); }
        catch (Exception ex) { Fail("That world could not be backed up.", ex); }
        finally { EndBusy(); }
    }

    /// <summary>
    /// Deletes a save from disk after a confirmation that says how much is about to go.
    /// </summary>
    /// <remarks>The size in the prompt helps confirm it is the intended world.</remarks>
    private async Task DeleteSaveAsync(WorldInfo save)
    {
        try
        {
            if (InUse(save.SourcePackId, "deleting this world")) return;

            var backups = App.State.Worlds.ListBackups(save).Count;
            var backupNote = backups == 0
                ? "\n\nThere are no backups of this world."
                : $"\n\n{backups} backup(s) of it will be kept in the instance's backups folder.";

            if (!await AppDialog.ConfirmAsync(_shell, "Delete world",
                    $"Delete '{save.DisplayName}' from disk?\n\n"
                    + $"{FormatSize(save.SizeBytes)} in {save.FolderPath}\n"
                    + "This removes the save folder permanently." + backupNote,
                    "Delete world", "Cancel", danger: true))
                return;

            var ct = BeginBusy($"Deleting {save.DisplayName}...");
            try
            {
                await App.State.Worlds.DeleteAsync(save, ct);
                EndBusy();
                ForgetFolderMembership(save.Key);
                App.State.Settings.Save();
                // Checked before the reload, which may change the group, so the message can say where
                // it was deleted from.
                var alsoElsewhere = CopyCountOf(save) > 1;
                ScanCaches.InvalidatePack(save.SourcePackId, ScanScope.Worlds);
                await LoadAsync(force: true);
                Okay(alsoElsewhere
                    ? $"Deleted {save.DisplayName} from {save.SourcePackName}. The other instances' "
                      + "saves of that folder name are untouched."
                    : $"Deleted {save.DisplayName}.");
            }
            finally { EndBusy(); }
        }
        catch (OperationCanceledException) { Okay("Delete cancelled."); }
        catch (IOException)
        {
            Fail("Close Minecraft before deleting this world - its files are in use.");
        }
        catch (Exception ex) { Fail("That world could not be deleted.", ex); }
    }

    /// <summary>Shows one save's folder in Explorer.</summary>
    /// <remarks>Takes a path instead of the row because a row can cover several saves; the menu asks
    /// which one (see <see cref="PerCopy"/>).</remarks>
    private void Reveal(string? path)
    {
        try
        {
            if (path is null || (!File.Exists(path) && !Directory.Exists(path)))
            {
                Fail("That file is no longer there.");
                return;
            }
            if (!SafeLaunch.RevealFile(path)) Fail("Explorer could not be opened.");
        }
        catch (Exception ex) { Fail("Explorer could not be opened.", ex); }
    }

    // ── drag and drop ────────────────────────────────────────────────────────

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedWorlds(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Dropping a world here goes straight into the Import flow, with the file already chosen.
    /// </summary>
    /// <remarks>The dropped file is passed as <see cref="ImportRequest.PrePickedPath"/> so the card
    /// doesn't ask for it again. One at a time, since the card walks through a single world.</remarks>
    private async void OnListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            var paths = DroppedWorlds(e);
            if (paths.Count == 0) return;
            if (paths.Count > 1)
                Okay($"Importing {Path.GetFileName(paths[0])} - drop them one at a time to give each its "
                   + "own instances.");
            await RunImportAsync(paths[0]);
        }
        catch (Exception ex) { Fail("Those files could not be imported.", ex); }
    }

    /// <summary>The dropped paths that could plausibly be a world: a folder with a level.dat, or a
    /// .zip.</summary>
    private static List<string> DroppedWorlds(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return new();
        return paths.Where(p =>
            (Directory.Exists(p) && WorldService.IsWorldFolder(p))
            || (File.Exists(p) && Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // ── instance state and connectivity ──────────────────────────────────────

    /// <summary>
    /// Keeps the running badges up to date, and drops the remembered scan for an instance whose game
    /// just closed.
    /// </summary>
    /// <remarks>Minecraft rewrites level.dat and every touched region file on exit, so a scan from
    /// before the session is stale. The launch service has no hook for this, so the page does it.</remarks>
    private void OnInstanceStateChanged(Guid packId)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var busy = App.State.Instances.IsBusy(packId);
            var was = _running.Contains(packId);
            if (busy) _running.Add(packId); else _running.Remove(packId);
            if (was == busy) return;

            // Only when the game closed. Nothing has been written at start, and dropping the scan
            // then would re-walk every region file while the game is booting.
            if (was && !busy) ScanCaches.InvalidatePack(packId, ScanScope.Worlds);

            // Either way the page re-reads: that is what repaints the "the game is running here"
            // sentences on every row of that instance.
            if (IsLoaded && IsVisible) _ = LoadAsync();
        });
    }

    /// <summary>Coming back online is worth one automatic re-read: the instance list we drew from the
    /// cache may have been missing an instance the whole time.</summary>
    private void OnConnectivityChanged()
    {
        if (!IsLoaded || App.State.IsOffline) return;
        if (_state.Kind is PageStateKind.Offline || _staleNote is not null) _ = LoadAsync();
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

        // The rows take no keyboard focus, so these act on the row the user last touched.
        if (WorldList.Focused is not { } row) return;
        if (SearchBox.IsExpanded && SearchBox.IsKeyboardFocusWithin) return;
        // Another page's text box (the side panel) can have the keyboard while this page is visible.
        if (TypingFocus.IsTyping) return;

        try
        {
            switch (e.Key)
            {
                // Both write to one save, and a row can cover several. Rather than guess which copy
                // to rename or delete, a multi-copy row is refused with a pointer to the menu.
                case Key.Delete:
                    e.Handled = true;
                    if (PickOneCopy(row, "deleted") is { } save) await DeleteSaveAsync(save);
                    break;
                case Key.F2:
                    e.Handled = true;
                    if (PickOneCopy(row, "renamed") is { } toRename) await RenameSaveAsync(toRename);
                    break;
                case Key.Enter:
                    e.Handled = true;
                    OpenRow(row);
                    break;
            }
        }
        catch (Exception ex) { Fail("That did not work.", ex); }
    }

    /// <summary>
    /// The save a keyboard shortcut may write to, or null after showing why not.
    /// </summary>
    /// <remarks>A world in several instances has several saves behind one Del, and picking one silently
    /// could delete the wrong one. The choice is made by name on the menu, and the refusal says
    /// so.</remarks>
    private WorldInfo? PickOneCopy(ContentRow row, string verb)
    {
        if (GroupOf(row) is not { } group) return null;
        if (group.Copies.Count == 1) return group.Copies[0];

        // With an instance in scope the answer is unambiguous: the row speaks for that instance's
        // copy, and the confirmation shows the full path.
        if (ScopedPack() is not null) return group.Primary;

        Fail($"{group.Copies.Count} instances have a save of this name, and they are "
           + $"{group.Copies.Count} separate saves - open the row's '...' menu and pick which one to "
           + $"have {verb}.");
        return null;
    }

    // ── busy state and the status line ───────────────────────────────────────

    /// <summary>Puts the page into its working state and hands back the token the work runs under.</summary>
    private CancellationToken BeginBusy(string label)
    {
        _busyCts?.Cancel();
        _busyCts = new CancellationTokenSource();
        _state.Begin(label, refreshing: true);
        return _busyCts.Token;
    }

    private void EndBusy()
    {
        _busyCts?.Dispose();
        _busyCts = null;
    }

    /// <summary>A percentage sink for the multi-gigabyte operations, reported into the status line.</summary>
    /// <remarks>A long copy with only an indeterminate bar looks like a hang. <see cref="PageState"/>
    /// owns the bar itself.</remarks>
    private IProgress<double> Percent(string what) =>
        new Progress<double>(f => _state.Progress($"{what} - {f:P0}"));

    /// <summary>
    /// True, with a message, when Minecraft currently has this instance open.
    /// </summary>
    /// <remarks>Every path that writes inside a save checks this first. The game holds session.lock and
    /// rewrites region files continuously, so writing underneath it gives an IOException or a
    /// half-written save.</remarks>
    private bool InUse(Guid packId, string what)
    {
        if (!App.State.Instances.IsBusy(packId)) return false;
        var name = _packs.FirstOrDefault(p => p.Id == packId)?.Name ?? "That instance";
        Fail($"{name} is running - close Minecraft before {what}.");
        return true;
    }

    private void Okay(string message) => _state.Note(message);

    /// <summary>Shows a message the user can act on. PageState sends the exception to the launcher
    /// log instead of the page.</summary>
    private void Fail(string message, Exception? ex = null) => _state.Error(message, ex);
}

// ── view models ──────────────────────────────────────────────────────────────

/// <summary>
/// One world as the persistent scan cache stores it.
/// </summary>
/// <remarks>Only what the walk found. The display name is a launcher setting and is re-read on every
/// paint. Changing this shape would throw away every user's remembered scan.</remarks>
public sealed record CachedWorld(
    string Folder,
    long SizeBytes,
    DateTimeOffset LastModified,
    string? LevelName,
    long? Seed,
    int? GameType,
    int? Difficulty,
    bool Hardcore,
    bool AllowCommands,
    string? VersionName,
    DateTimeOffset? LastPlayed);

/// <summary>One chip in the folder strip.</summary>
/// <remarks>No brushes: the selected look is a trigger on <see cref="IsActive"/>, so accent changes
/// repaint chips already on screen. Mutable and notifying because the strip is diffed; a Reset would
/// replay the entrance animation on every search keystroke.</remarks>
public sealed class WorldFolderChipVm : INotifyPropertyChanged
{
    private string _countLabel;
    private bool _isActive;
    private bool _isUserFolder;

    /// <param name="id">The folder name, "" for All, or one of the page's reserved <c>:</c> ids.</param>
    /// <param name="label">What the chip says.</param>
    /// <param name="icon">An MDL2 glyph, or empty.</param>
    /// <param name="count">How many rows are in it; zero shows no count.</param>
    /// <param name="isActive">True for the chip the page is currently filtered by.</param>
    /// <param name="tip">The chip's tooltip.</param>
    /// <remarks>Unlike the Resource packs and Shader packs chips, a world folder has no rule; it only
    /// groups.</remarks>
    public WorldFolderChipVm(string id, string label, string icon, int count, bool isActive, string tip)
    {
        Id = id;
        Label = label;
        Icon = icon;
        ToolTipText = tip;
        _countLabel = count > 0 ? $"({count})" : "";
        _isActive = isActive;
        // Use ContentFolderRules.CanEdit, the same check rename and delete use, so the strip
        // agrees with them.
        _isUserFolder = ContentFolderRules.CanEdit(LibraryKind.World, id);
    }

    /// <inheritdoc cref="WorldFolderChipVm(string,string,string,int,bool,string)"/>
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

    /// <summary>The chip the page is filtered by. A bool rather than a brush (see the class
    /// remarks).</summary>
    public bool IsActive
    {
        get => _isActive;
        private set { if (_isActive != value) { _isActive = value; Raise(nameof(IsActive)); } }
    }

    /// <summary>
    /// True for a folder the user owns, the only kind that can be renamed or deleted.
    /// </summary>
    /// <remarks>All and New folder are computed views. Answered by
    /// <see cref="ContentFolderRules.CanEdit"/>, the same check rename and delete use.</remarks>
    public bool IsUserFolder
    {
        get => _isUserFolder;
        private set { if (_isUserFolder != value) { _isUserFolder = value; Raise(nameof(IsUserFolder)); } }
    }

    /// <summary>True for the trailing "New folder" chip, which makes a folder rather than
    /// selecting one. The shared chip style triggers on this to draw it as an outline.</summary>
    public bool IsNewAction => Id == ":new";

    /// <summary>Folds a freshly built chip into this one, for <see cref="ListDiff.Apply"/>.</summary>
    /// <remarks>Every property that can change must be copied here: the strip is diffed, so anything
    /// left out would stop updating after the first build.</remarks>
    public void CopyFrom(WorldFolderChipVm fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        CountLabel = fresh.CountLabel;
        IsActive = fresh.IsActive;
        IsUserFolder = fresh.IsUserFolder;
    }

    /// <inheritdoc cref="IsActive"/>
    public void SetActive(bool active) => IsActive = active;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One entry in the instance scope box.</summary>
public sealed record WorldScopeItem(Guid? Id, string Label);

/// <summary>
/// One save as a card, for the Worlds tab of an instance's page (<c>PackDetailView</c>).
/// </summary>
/// <remarks><see cref="CoverFor"/> and <see cref="LoadWorldIcon"/> are shared with
/// <c>WorldDetailView</c> and the Worlds list, so a world looks the same everywhere.</remarks>
public sealed class WorldRow : INotifyPropertyChanged
{
    private readonly ImageSource? _iconImage;

    /// <summary>Instances that hold a save folder of the same name, i.e. copies made by "Play with..."
    /// or by copying the world into another instance.</summary>
    private readonly IReadOnlyList<string> _copiesIn;

    public WorldInfo Source { get; }
    public IReadOnlyList<PackSummary> AllPacks { get; }
    public string DisplayName => Source.DisplayName;
    public string SourcePackName => Source.SourcePackName;
    public string SizeLabel => FormatSize(Source.SizeBytes);
    public ImageSource? IconImage => _iconImage;
    public Visibility IconImageVisibility => _iconImage is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackVisibility => _iconImage is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The badge over the cover: the game mode.</summary>
    public string GameModeBadge => Source.Info.GameType is null ? "WORLD" : Source.Info.GameModeLabel.ToUpperInvariant();
    public Visibility HardcoreVisibility => Source.Info.Hardcore ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CheatsVisibility => Source.Info.AllowCommands && !Source.Info.Hardcore
        ? Visibility.Visible : Visibility.Collapsed;
    public bool HasSeed => Source.Info.HasSeed;

    /// <summary>Minecraft has this save open right now, so nothing may be written inside it.</summary>
    public Visibility RunningVisibility => App.State.Instances.IsBusy(Source.SourcePackId)
        ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Called when the instance this save lives in starts or stops, so the badge appears and
    /// disappears without rebuilding every card.</summary>
    public void NotifyRunningChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningVisibility)));

    /// <summary>The line under the world's name: when it was last played, and what wrote it.</summary>
    public string MetaLabel
    {
        get
        {
            var when = Source.Info.LastPlayed is { } played
                ? $"Played {TimeFormat.DateTime(played)}"
                : $"Modified {TimeFormat.DateTime(Source.LastModified)}";
            return Source.Info.VersionName is { Length: > 0 } v ? $"{when} · MC {v}" : when;
        }
    }

    /// <summary>
    /// The save's folder, or where else a save of that name lives.
    /// </summary>
    /// <remarks>The folder is shown because two saves can have the same display name and only the
    /// folder tells them apart.</remarks>
    public string CompatibilityLabel =>
        _copiesIn.Count == 0
            ? $"saves/{Source.FolderName}"
            : $"saves/{Source.FolderName} · also in {string.Join(", ", _copiesIn)}";

    /// <summary>Everything about the world that does not fit on the card.</summary>
    public string ToolTipText
    {
        get
        {
            var info = Source.Info;
            var lines = new List<string>
            {
                Source.DisplayName,
                $"Folder: {Source.FolderPath}",
                $"Instance: {Source.SourcePackName}",
                $"Size: {FormatSize(Source.SizeBytes)}",
                $"Mode: {info.GameModeLabel} · {info.DifficultyLabel}" +
                    (info.Hardcore ? " · hardcore" : "") + (info.AllowCommands ? " · cheats on" : ""),
                $"Minecraft: {info.VersionLabel}",
                $"Seed: {info.SeedLabel}"
            };
            lines.Add(info.LastPlayed is { } played
                ? $"Last played: {TimeFormat.LongDateTime(played)}"
                : $"Last modified: {TimeFormat.LongDateTime(Source.LastModified)}");
            if (_copiesIn.Count > 0) lines.Add($"Copies live in: {string.Join(", ", _copiesIn)}");
            return string.Join("\n", lines);
        }
    }

    public WorldRow(WorldInfo info, IReadOnlyList<PackSummary> allPacks, IReadOnlyList<WorldInfo>? allWorlds = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        Source = info;
        AllPacks = allPacks;
        _iconImage = LoadWorldIcon(info.FolderPath);
        _copiesIn = allWorlds is null
            ? Array.Empty<string>()
            : allWorlds
                .Where(w => w.SourcePackId != info.SourcePackId
                            && string.Equals(w.FolderName, info.FolderName, StringComparison.OrdinalIgnoreCase))
                .Select(w => w.SourcePackName)
                .Distinct()
                .ToList();
    }

    public string Initial
    {
        get
        {
            var s = Source.DisplayName?.TrimStart() ?? "";
            return s.Length > 0 ? s[..1].ToUpperInvariant() : "?";
        }
    }

    public Brush CoverBackground => CoverFor(Source.DisplayName);

    /// <summary>
    /// The card art for a world with no icon.png: a gradient chosen by hashing its name.
    /// </summary>
    /// <remarks>Deterministic, so a world keeps its colour between launches and matches its detail
    /// hero. Distinct per world, since a wall of identical placeholders is unreadable.</remarks>
    public static Brush CoverFor(string? name)
    {
        var palette = new (Color from, Color to)[]
        {
            (Color.FromRgb(0xC0, 0x30, 0x30), Color.FromRgb(0x7A, 0x1E, 0x1E)),
            (Color.FromRgb(0x4F, 0x9C, 0xF9), Color.FromRgb(0x1E, 0x4F, 0x8C)),
            (Color.FromRgb(0x10, 0xB9, 0x81), Color.FromRgb(0x0A, 0x6B, 0x4A)),
            (Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0x5F, 0x2C, 0x9A)),
            (Color.FromRgb(0xE3, 0xB3, 0x41), Color.FromRgb(0x8E, 0x6A, 0x18)),
            (Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0x0B, 0x6E, 0x65)),
            (Color.FromRgb(0xEC, 0x4F, 0x88), Color.FromRgb(0x8E, 0x25, 0x4E)),
            (Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0x96, 0x42, 0x0B)),
        };

        var h = 0u;
        foreach (var c in name ?? "") h = h * 31 + char.ToLowerInvariant(c);
        var (a, b) = palette[h % (uint)palette.Length];
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(a, 0.0),
                new GradientStop(b, 1.0)
            }
        };
        gradient.Freeze();
        return gradient;
    }

    /// <summary>
    /// Decoded world icons, keyed by path and stamped with the file's write time, so rows built twice
    /// per open don't decode the same icon twice. The bitmaps are frozen, so sharing them is safe.
    /// </summary>
    /// <remarks>Locked because the rescan builds rows on a worker while <c>PackDetailView</c> builds cards
    /// on the UI thread; an unsynchronised Dictionary can hang or corrupt under concurrent use.</remarks>
    private static readonly Dictionary<string, (DateTime Stamp, ImageSource? Image)> IconCache = new();

    private static readonly object IconGate = new();

    /// <summary>The save's own icon.png, or null when it has none. Shared with the detail hero and
    /// with the Worlds list.</summary>
    public static ImageSource? LoadWorldIcon(string folderPath)
    {
        var iconPath = Path.Combine(folderPath, "icon.png");

        DateTime stamp;
        try
        {
            var info = new FileInfo(iconPath);
            if (!info.Exists) return null;
            stamp = info.LastWriteTimeUtc;
        }
        catch { return null; }

        lock (IconGate)
            if (IconCache.TryGetValue(iconPath, out var hit) && hit.Stamp == stamp) return hit.Image;

        ImageSource? image = null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            // The row icon is 40px and the card cover about 220px, so decoding a 1024px image at
            // full size would only waste memory.
            bitmap.DecodePixelWidth = 320;
            bitmap.UriSource = new Uri(iconPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch { image = null; }

        // Two threads racing here decoded the same file, so it doesn't matter which one wins.
        lock (IconGate) IconCache[iconPath] = (stamp, image);
        return image;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
      : bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
      : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
      : $"{bytes} B";
}
