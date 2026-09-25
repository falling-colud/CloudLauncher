using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>
/// Every shader pack on this PC, in a list, grouped into folders you make.
/// </summary>
/// <remarks>
/// <para>The row's circle is the pack's "on by default" bit. An instance has one shader slot, so
/// choosing a pack unchooses the previous one, and clicking the chosen pack again means no shader.</para>
/// <para>Without Iris, Oculus or OptiFine an activated shader is a config nothing reads, so the page
/// warns (banner, NEEDS IRIS pill) and the defaults engine never activates there.</para>
/// <para>The rescan is unconditional: the active pack lives in <c>config/iris.properties</c>, which a
/// fingerprint of <c>shaderpacks/</c> would not notice.</para>
/// </remarks>
public partial class ShaderPacksView : Page, IReusablePage, IRefreshablePage
{
    private readonly MainWindow _shell;
    private readonly ObservableCollection<ShaderFolderChipVm> _folderChips = new();

    /// <summary>Everything the scan found; <see cref="ApplyFilter"/> narrows it.</summary>
    private readonly ObservableCollection<ContentRow> _all = new();

    /// <summary>Guards the scope ComboBox against its own fill: setting ItemsSource and then
    /// SelectedItem raises SelectionChanged synchronously.</summary>
    private readonly Reentrancy _filling = new();

    private readonly PageState _state;

    /// <summary>The disk pass.</summary>
    private CancellationTokenSource? _scanCts;

    /// <summary>A download started from this page (Update). Separate from the scan so cancelling
    /// one does not cancel the other.</summary>
    private CancellationTokenSource? _workCts;

    /// <summary>The store browser open in the side panel, if any. Kept so its Installed handler can be
    /// removed on Unloaded, since the panel can outlive this page.</summary>
    private ShaderBrowserView? _browser;

    private List<PackSummary> _packs = new();
    private ShaderModel _model = new();
    private ShaderSortMode _sortMode;
    private string? _activeFolder;
    private Guid? _scopePackId;

    /// <summary>Why the instance list may be out of date (offline). <c>_cacheNote</c> is the provenance
    /// line <see cref="PageRefresh"/> writes while cached rows are shown.</summary>
    private string? _staleNote;
    private string? _cacheNote;

    /// <summary>Id of the trailing "New folder" chip. The leading colon is refused by
    /// <see cref="IsReservedFolderName"/>, so it can't collide with a user folder.</summary>
    private const string NewFolderChipId = ":new";

    /// <summary>The plus on the "New folder" chip. The shared chip style draws that chip outlined
    /// (FolderChipBorder in Themes/Controls.xaml).</summary>
    private const string NewFolderGlyph = "";

    /// <summary>What the last scan found, so reopening the page paints in its first frame.</summary>
    private static readonly ScanCache<CachedShaderPack> Cache =
        ScanCaches.For<CachedShaderPack>(ScanKinds.Shaders);

    public ShaderPacksView(MainWindow shell)
    {
        InitializeComponent();
        _shell = shell;
        _sortMode = App.State.Settings.ShaderSortMode;
        FolderStrip.ItemsSource = _folderChips;

        PackList.Configure(new ContentListHost
        {
            Open = OpenRow,
            Toggled = ToggleRowAsync,
            // A circle instead of a switch: an instance has one shader slot, so "on by default" is a
            // single choice.
            RowControl = ContentRowControl.Choice,
            Menu = BuildRowMenu,
            Status = Okay
        });

        _state = new PageState(PackList, PageStateHost, nameof(ShaderPacksView))
            .Copy(PageCopy.Shaders)
            .Slots(CountLabel, StatusLabel, BusyBar, BusyCancelButton)
            .DisableWhileBusy(RefreshButton, SortButton, PackScopeBox);
        _state.EmptyCopy(
            "No shader packs yet",
            "Import a shader zip you already have, or find one in the store. Anything an instance "
            + "already has shows up here too, and one click is all it takes to have it handed to "
            + "every compatible instance. A shader pack needs Iris (or Oculus on Forge) in the "
            + "instance to do anything at all.",
            "Import a shader pack",
            () => OnImport(this, new RoutedEventArgs()));
        _state.RetryRequested += () => _ = LoadAsync(force: true);
        _state.CancelRequested += () =>
        {
            _workCts?.Cancel();
            _scanCts?.Cancel();
            _state.Cancelled("Scan stopped.");
        };

        SearchBox.TextChangedDebounced += (_, _) => ApplyFilter();

        // Paired with the Unloaded removals below. The page is reused (IReusablePage), so subscribing
        // in the constructor would leave these dead after the first navigation.
        Loaded += async (_, _) =>
        {
            if (Window.GetWindow(this) is { } w) w.PreviewKeyDown += GlobalKeys;
            App.State.ConnectivityChanged += OnConnectivityChanged;
            await LoadAsync();
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } w) w.PreviewKeyDown -= GlobalKeys;
            App.State.ConnectivityChanged -= OnConnectivityChanged;
            // The side panel can outlive this page, and a leftover handler would keep rescanning.
            if (_browser is not null) _browser.Installed -= OnBrowserInstalled;
            _scanCts?.Cancel();
            _workCts?.Cancel();
        };
    }

    // ── loading ──────────────────────────────────────────────────────────────

    /// <summary>What one pass of the scan found. Assigned once, at the end of the worker.</summary>
    private sealed class ShaderModel
    {
        public List<LibraryItem> Library { get; init; } = [];
        public List<ShaderInstalled> Installed { get; init; } = [];
        public Dictionary<Guid, InstanceContentOverrides> Overrides { get; init; } = new();

        /// <summary>Which shader loader each instance has. Empty on the cached paint, since detecting
        /// a loader enumerates <c>mods/</c>. A missing instance means unknown, not "no loader".</summary>
        public Dictionary<Guid, ShaderLoader> Loaders { get; init; } = new();

        /// <summary>The pack each instance is set to load (from the loader's config), or null.</summary>
        public Dictionary<Guid, string?> Active { get; init; } = new();

        /// <summary>Row key -> the numbers behind it. Filter and sort use these, not row text.</summary>
        public Dictionary<string, ShaderFacts> Facts { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset? ScannedUtc { get; init; }
        public int Failed { get; init; }
    }

    /// <summary>The counts behind one row.</summary>
    /// <param name="Present">Instances holding this item's bytes (1 for an instance's own file).</param>
    /// <param name="Loaded">Of those, the ones whose shader config names it.</param>
    /// <param name="Reach">Instances this pack's circle currently puts it in. Zero when it is off.</param>
    /// <param name="Blind">Of <paramref name="Present"/>, the ones with no shader loader at all.</param>
    private sealed record ShaderFacts(
        int Present, int Loaded, int Reach, int Blind, long SizeBytes, DateTimeOffset Modified);

    /// <summary>One shader pack found inside one instance.</summary>
    private sealed record ShaderInstalled(PackSummary Pack, ShaderPackInfo Info);

    /// <inheritdoc cref="IRefreshablePage.RefreshAsync"/>
    /// <remarks>Re-reads the instance list and rescans, keeping rows, scroll and selection.</remarks>
    public Task RefreshAsync() => LoadAsync();

    /// <summary>
    /// Paints the cached scan, then re-reads the library and every instance underneath it.
    /// </summary>
    /// <param name="force">Skip the cache and re-walk the folders (Refresh, F5, and after this page
    /// writes a file).</param>
    private async Task LoadAsync(bool force = false)
    {
        // A fresh source every pass, or the reused page would come back with a cancelled token.
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
            failed: "Your shader packs could not be read.",
            cachedNote: age => age is { Length: > 0 }
                ? $"Showing the last scan ({age}) - re-reading every instance now."
                : "Showing the last scan - re-reading every instance now.",
            quiet: !force);

        if (!ok) return;

        if (_model.Failed > 0)
            _state.Note($"{_model.Failed} instance(s) could not be read - see the launcher log.");
        else if (_staleNote is { Length: > 0 } why)
            _state.Note(why);
    }

    /// <summary>The cheap setup the cached paint needs: the cache file, the instance list, and the
    /// scope box built from it.</summary>
    /// <remarks>Hidden instances are included: the reconciler places into every instance, and leaving
    /// them out would make the coverage column disagree with what actually happens.</remarks>
    private async Task PrepareAsync(CancellationToken ct)
    {
        await Cache.EnsureLoadedAsync();

        // ListPacksAsync already falls back server -> cached list -> disk scan, so this rarely fails;
        // when it does, the last list we had is still fine to scan against.
        _staleNote = null;
        try
        {
            // Reuse the list the Instances page just fetched, if any, to skip a round trip.
            _packs = await App.State.Api.ListPacksQuickAsync(ct);
            _staleNote = App.State.Api.PackListStale is { Length: > 0 } why
                ? $"Showing shaders for your last known instances - {why}."
                : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (OfflineException ox)
        {
            _staleNote = ox.Reason is { Length: > 0 } reason
                ? $"Showing shaders for your last known instances - {reason}."
                : "Showing shaders for your last known instances.";
        }
        catch (Exception ex)
        {
            AppLog.LogError(nameof(ShaderPacksView), ex);
            _staleNote = "The instance list could not be refreshed; scanning the instances already known.";
        }

        RebuildScopeBox();
    }

    /// <summary>
    /// The cached list, built without walking any folders. This is what the first frame draws.
    /// </summary>
    /// <remarks>Two cheap things are still read live: whether the instance folder exists and which pack
    /// it is set to load. The loader isn't (that means enumerating <c>mods/</c> on the UI thread), so
    /// the NEEDS IRIS warnings wait for the real scan.</remarks>
    private CachedRows<ContentRow> Remembered()
    {
        try
        {
            var installed = new List<ShaderInstalled>();
            var active = new Dictionary<Guid, string?>();
            DateTimeOffset? oldest = null;

            foreach (var pack in _packs)
            {
                string gameDir, localDir;
                try
                {
                    gameDir = App.State.Shaders.FolderFor(pack.Id);
                    localDir = App.State.Shaders.LocalFolderFor(pack.Id);
                }
                catch { continue; }

                var hit = Cache.Get(ScanCache<CachedShaderPack>.ScopeKey(pack.Id));
                if (hit.State != ScanState.Cached || hit.Rows.Count == 0) continue;
                // The instance folder is gone, so don't paint its packs from the cache.
                if (!RootFingerprint.Shallow(gameDir).Exists && !RootFingerprint.Shallow(localDir).Exists)
                    continue;

                var loaded = SafeActive(pack);
                active[pack.Id] = loaded;
                foreach (var row in hit.Rows)
                {
                    // Cached names become paths for Delete/Replace; they must be plain file names.
                    if (!PathSafety.IsSafeFileName(row.FileName))
                    {
                        AppLog.Log(nameof(ShaderPacksView), $"Ignored a remembered shader pack with an unusable name: {row.FileName}");
                        continue;
                    }
                    installed.Add(new ShaderInstalled(pack, Rehydrate(row, pack, gameDir, localDir, loaded)));
                }
                if (hit.ScannedUtc is { } at && (oldest is null || at < oldest)) oldest = at;
            }

            var model = new ShaderModel
            {
                Library = SafeLibraryScan(),
                Installed = installed,
                Overrides = LoadOverrides(_packs),
                Active = active,
                ScannedUtc = oldest
            };
            if (model.Library.Count == 0 && installed.Count == 0) return CachedRows<ContentRow>.None;

            _model = model;
            return new CachedRows<ContentRow>(BuildRows(model, cheap: true), oldest);
        }
        catch (Exception ex)
        {
            // An empty cache is fine; the real scan follows.
            AppLog.LogError(nameof(ShaderPacksView), ex);
            return CachedRows<ContentRow>.None;
        }
    }

    /// <summary>
    /// The real scan: the library, then every instance's two shaderpacks folders and its shader config.
    /// </summary>
    /// <remarks>Runs on a worker thread, since sizing an unpacked pack means thousands of file stats.
    /// There is no fingerprint gate: the active pack lives in <c>config/iris.properties</c>, which a walk
    /// of <c>shaderpacks/</c> can't see. The fingerprint only decides whether to rewrite the cache
    /// (see <see cref="Remember"/>).</remarks>
    private IReadOnlyList<ContentRow> Rescan(bool force, CancellationToken ct)
    {
        var library = SafeLibraryScan();
        var installed = new List<ShaderInstalled>();
        var loaders = new Dictionary<Guid, ShaderLoader>();
        var active = new Dictionary<Guid, string?>();
        DateTimeOffset? oldest = null;
        var failed = 0;

        foreach (var pack in _packs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var loader = App.State.Shaders.DetectLoader(pack.Id, pack.Name);
                loaders[pack.Id] = loader;
                // From the loader's config, not the rows: the config may name a pack that has since
                // been deleted, and the page has to show that.
                active[pack.Id] = App.State.Shaders.ActiveShader(pack.Id, pack.Name);

                var found = App.State.Shaders.ScanPack(pack.Id, pack.Name);
                foreach (var info in found) installed.Add(new ShaderInstalled(pack, info));

                Remember(pack.Id, found, loader, active[pack.Id], force);
                oldest ??= DateTimeOffset.Now;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A never-launched instance has no shaderpacks folder. Count a failure only when the
                // folder exists and couldn't be read.
                AppLog.LogError(nameof(ShaderPacksView), ex);
                if (HasShaderFolder(pack)) failed++;
            }
        }

        var model = new ShaderModel
        {
            Library = library,
            Installed = installed,
            Overrides = LoadOverrides(_packs),
            Loaders = loaders,
            Active = active,
            ScannedUtc = oldest,
            Failed = failed
        };

        // A pass superseded during the last instance must not publish its model over the newer one.
        ct.ThrowIfCancellationRequested();
        _model = model;
        return BuildRows(model, cheap: false);
    }

    /// <summary>Writes this instance's rows into the scan cache, only when they differ from what is
    /// already cached, so an ordinary page open doesn't rewrite the file.</summary>
    /// <remarks>The active pack and loader are part of the stamp. They aren't in the rows, so without
    /// them switching shaders would leave the cache unchanged and the next cold open would show the
    /// old active pack.</remarks>
    private static void Remember(Guid packId, List<ShaderPackInfo> found, ShaderLoader loader,
                                 string? active, bool force)
    {
        var key = ScanCache<CachedShaderPack>.ScopeKey(packId);
        var stamp = new Fingerprint()
            .Add("packs", $"{found.Count}:{found.Sum(s => s.SizeBytes)}:{found.Select(s => s.LastModified.Ticks).DefaultIfEmpty(0).Max()}")
            .Add("active", active is { Length: > 0 } ? active : "-")
            .Add("loader", loader.ToString())
            .ToString();

        if (!force && Cache.Get(key).Fingerprint == stamp) return;
        Cache.Put(key, found.Select(ToCached).ToList(), stamp);
    }

    private static CachedShaderPack ToCached(ShaderPackInfo s) =>
        new(s.FileName, s.IsFolder, s.LastModified.Ticks, s.SizeBytes, s.HasSettings, (byte)s.Origin);

    /// <summary>Turns a cached row back into a full one. Anything not cached is either derivable
    /// (paths, key) or already in memory (display name, provenance).</summary>
    private static ShaderPackInfo Rehydrate(CachedShaderPack row, PackSummary pack,
                                            string gameDir, string localDir, string? active)
    {
        var origin = (ShaderPackOrigin)row.Origin;
        var key = ShaderPackService.Key(pack.Id, row.FileName, origin);
        var stored = App.State.Settings.GetShaderPack(key);
        var dir = origin == ShaderPackOrigin.Local ? localDir : gameDir;

        return new ShaderPackInfo(
            Key: key,
            SourcePackId: pack.Id,
            SourcePackName: pack.Name,
            FileName: row.FileName,
            FilePath: Path.Combine(dir, row.FileName),
            DisplayName: stored is { DisplayName.Length: > 0 } ? stored.DisplayName
                                                               : ShaderPackService.PrettyName(row.FileName),
            IsFolder: row.IsFolder,
            IsActive: string.Equals(active, row.FileName, StringComparison.OrdinalIgnoreCase),
            LastModified: new DateTime(row.ModifiedTicks),
            SizeBytes: row.SizeBytes,
            HasSettings: row.HasSettings,
            // Iris writes a pack's tuning beside the copy it loaded, which is always the game/ one.
            SettingsPath: Path.Combine(gameDir, row.FileName + ".txt"),
            Source: stored?.Source,
            ProjectId: stored?.ProjectId,
            VersionId: stored?.VersionId,
            VersionNumber: stored?.VersionNumber,
            Origin: origin);
    }

    private static string? SafeActive(PackSummary pack)
    {
        try { return App.State.Shaders.ActiveShader(pack.Id, pack.Name); }
        catch { return null; }
    }

    /// <summary>True when either of this instance's shaderpacks folders exists. Separates "never
    /// launched" from "could not be read".</summary>
    private static bool HasShaderFolder(PackSummary pack)
    {
        try
        {
            return RootFingerprint.Shallow(App.State.Shaders.FolderFor(pack.Id)).Exists
                   || RootFingerprint.Shallow(App.State.Shaders.LocalFolderFor(pack.Id)).Exists;
        }
        catch { return false; }
    }

    private static List<LibraryItem> SafeLibraryScan()
    {
        try { return App.State.Library.Scan(LibraryKind.ShaderPack); }
        catch (Exception ex) { AppLog.LogError(nameof(ShaderPacksView), ex); return []; }
    }

    /// <summary>Each instance's overrides for the defaults, one small JSON file per instance. A missing
    /// file means nothing has been decided.</summary>
    private static Dictionary<Guid, InstanceContentOverrides> LoadOverrides(IReadOnlyList<PackSummary> packs)
    {
        var map = new Dictionary<Guid, InstanceContentOverrides>();
        foreach (var pack in packs)
        {
            try { map[pack.Id] = App.State.ContentDefaults.OverridesFor(pack.Id); }
            catch (Exception ex) { AppLog.LogError(nameof(ShaderPacksView), ex); }
        }
        return map;
    }

    private void OnConnectivityChanged()
    {
        // Only the instance list comes from the server, so offline is just a note here.
        if (App.State.IsOffline && _state.Kind is not (PageStateKind.Loading or PageStateKind.Refreshing))
            _state.Note("The server is not answering. The shader packs themselves are on this PC.");
    }

    // ── building the rows ────────────────────────────────────────────────────

    /// <summary>
    /// One row per library item, plus the instances' own packs (just the scoped one's when scoped).
    /// </summary>
    /// <param name="cheap">True on the cached paint: match a library item to an instance's copy by file
    /// name only, since the real test opens both files.</param>
    private List<ContentRow> BuildRows(ShaderModel model, bool cheap)
    {
        var rows = new List<ContentRow>();
        var folded = new HashSet<string>(StringComparer.Ordinal);
        var scope = ScopedPack();

        foreach (var item in model.Library)
        {
            var present = new List<ShaderInstalled>();
            var shadowing = new List<ShaderInstalled>();
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
            rows.Add(new ContentRow(LibraryState(item, present, shadowing, model, scope)));
        }

        // Instances' own un-libraried packs. Rows stay per instance rather than folded by file name:
        // two same-named packs in two instances are two different files as far as we know.
        foreach (var installed in model.Installed)
        {
            if (scope is not null && installed.Pack.Id != scope.Id) continue;
            if (folded.Contains(installed.Info.Key)) continue;
            rows.Add(new ContentRow(InstanceState(installed, model)));
        }

        return rows;
    }

    /// <summary>Are these two paths the same bytes? Name-only on the cached paint.</summary>
    private static bool SameFile(string libraryPath, string instancePath, bool cheap)
    {
        if (cheap) return true;   // the caller has already matched the file name
        try { return PackFolderService.EntriesReferToSameContent(libraryPath, instancePath); }
        catch (Exception ex) { AppLog.LogError(nameof(ShaderPacksView), ex); return false; }
    }

    private ContentRowState LibraryState(
        LibraryItem item, List<ShaderInstalled> present, List<ShaderInstalled> shadowing,
        ShaderModel model, PackSummary? scope)
    {
        var chosen = item.Defaults is { Enabled: true };

        var loaded = present.Count(p => LoadsThis(model, p.Pack.Id, item.FileName));
        var blind = present.Count(p => IsBlind(model, p.Pack.Id));
        // Same answer the engine uses. The loader comes from the scan: shader placement depends on it,
        // and reading it here would enumerate a folder per item per instance.
        var reach = ContentPlacement.Reach(item, _packs, model.Overrides, LoaderOf);

        var here = scope is null ? null : present.FirstOrDefault(p => p.Pack.Id == scope.Id);
        var excludedHere = scope is not null && chosen
                           && !ContentPlacement.GoesInto(item, scope,
                                   model.Overrides.GetValueOrDefault(scope.Id), LoaderOf(scope));
        var loadedHere = scope is not null && LoadsThis(model, scope.Id, item.FileName);
        var otherHere = scope is null ? null : model.Active.GetValueOrDefault(scope.Id);

        // With a scope: does that instance lack a loader? Without: do any instances holding this pack?
        var loaderMissing = scope is not null ? IsBlind(model, scope.Id) : blind > 0;
        var blindNames = present.Where(p => IsBlind(model, p.Pack.Id)).Select(p => p.Pack.Name).ToList();

        model.Facts[item.Key] = new ShaderFacts(present.Count, loaded, reach, blind,
                                                item.SizeBytes, item.LastModified);

        var folder = FolderOf(item.Key);
        var tooltip = new List<string> { item.DisplayName, item.FileName };
        tooltip.Add(chosen
            ? $"The shader pack you want everywhere - {reach} of your {_packs.Count} instance(s) get it "
              + "and load it. Open its page to narrow that."
            : "Off: nothing places it anywhere until you choose it or put it into an instance "
              + "yourself.");
        tooltip.Add(present.Count == 0
            // Nothing is placed until each instance's next launch, so for a chosen pack "nowhere" means
            // "not yet".
            ? chosen
                ? "No instance holds a copy yet - each one takes its own the next time it starts."
                : "Not in any instance yet."
            : $"In {present.Count} instance(s), loaded by {loaded}: "
              + string.Join(", ", present.Select(p => LoadsThis(model, p.Pack.Id, item.FileName)
                  ? $"{p.Pack.Name} (loading it)"
                  : $"{p.Pack.Name} (present)")));
        if (shadowing.Count > 0)
            tooltip.Add($"{string.Join(", ", shadowing.Select(s => s.Pack.Name))} "
                      + "has its own file of that name, which wins at launch.");
        if (blindNames.Count > 0)
            tooltip.Add($"{string.Join(", ", blindNames)} cannot load a shader pack at all - no Iris, "
                      + "Oculus or OptiFine - so this file does nothing there.");

        return new ContentRowState
        {
            Key = item.Key,
            DisplayName = item.DisplayName,
            Item = item,
            Path = item.Path,
            MetaLine = $"{item.FileName}  ·  {item.SizeLabel}  ·  added {TimeFormat.Date(item.AddedAt)}",
            // A chosen pack with no copies yet is normal (placement happens at next launch), so it reads
            // "due in N at launch" rather than "in no instance".
            CoverageLine =
                present.Count > 0   ? $"loaded in {loaded} of {present.Count}"
                : !chosen           ? "in no instance"
                : _packs.Count == 0 ? "no instances yet"
                : reach == 0        ? "no instance ticked"
                :                     $"due in {reach} at launch",
            CoverageTip = (present.Count == 0
                              ? "No instance holds a copy of this yet. "
                              : $"In {present.Count} of your {_packs.Count} instance(s); {loaded} of them "
                                + "are set to load it - an instance can hold a dozen shader packs and "
                                + "load exactly one. ")
                        + (chosen
                            ? $"It is the one you chose, so {reach} instance(s) are due it - each one "
                              + "takes its copy the next time it starts, or use \"Place everything that "
                              + "is on\" on this row's menu to do it now. Open its page to untick any "
                              + "of them."
                            : "It is not chosen, so nothing places it for you."),
            Tooltip = string.Join("\n", tooltip),
            IsDefault = chosen,
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
            OffHere = scope is not null && (excludedHere || (here is not null && !loadedHere)),
            OffHereTip = scope is null
                ? ""
                : excludedHere
                    ? $"{scope.Name} is unticked on this pack's own page, so it is left alone there."
                    : otherHere is { Length: > 0 } other
                        ? $"It is in {scope.Name}, but {scope.Name} is set to load "
                          + $"{ShaderPackService.PrettyName(other)} instead."
                        : $"It is in {scope.Name}, but {scope.Name} is not loading any shader pack.",
            NeedsLoader = loaderMissing,
            LoaderLabel = "NEEDS IRIS",
            LoaderTip = scope is not null
                ? $"{scope.Name} has no Iris, Oculus or OptiFine, so a shader pack there does nothing "
                  + "at all - the game just renders vanilla and never says why."
                : blindNames.Count > 0
                    ? $"{string.Join(", ", blindNames)} cannot load a shader pack. Install Iris "
                      + "(Fabric/NeoForge) or Oculus (Forge) there first."
                    : "",
            // The pack's "on by default" bit; choosing it unchooses the previous pack.
            ToggleOn = chosen,
            CanToggle = true,
            ToggleTip = chosen
                ? $"{item.DisplayName} is the shader you want everywhere. Click to stop offering any "
                  + "shader - the copies already in your instances stay where they are."
                : $"Make {item.DisplayName} the shader every compatible instance gets and loads. "
                  + "Whichever pack holds that now gives it up, because an instance loads one.",
            CanOpen = true
        };
    }

    private ContentRowState InstanceState(ShaderInstalled installed, ShaderModel model)
    {
        var info = installed.Info;
        var pack = installed.Pack;
        var loadsIt = LoadsThis(model, pack.Id, info.FileName);
        var blind = IsBlind(model, pack.Id);
        var folder = FolderOf(info.Key);

        model.Facts[info.Key] = new ShaderFacts(1, loadsIt ? 1 : 0, 0, blind ? 1 : 0,
                                                info.SizeBytes, SafeStamp(info.LastModified));

        var tooltip = new List<string> { info.DisplayName, info.FileName, info.FilePath };
        tooltip.Add(loadsIt
            ? $"{pack.Name} is set to load this one."
            : $"In {pack.Name}'s folder, but {pack.Name} is not loading it.");
        tooltip.Add("Only in this instance so far. Choose it and every compatible instance gets the "
                  + "file and loads it.");
        if (info.ProvenanceLabel is { Length: > 0 } provenance) tooltip.Add(provenance);
        if (info.HasSettings) tooltip.Add("Has tuned Iris settings, which travel with it when you copy it.");
        if (blind) tooltip.Add($"{pack.Name} has no Iris, Oculus or OptiFine, so this does nothing there.");

        return new ContentRowState
        {
            Key = info.Key,
            DisplayName = info.DisplayName,
            Item = null,
            PackId = info.SourcePackId,
            Path = info.FilePath,
            MetaLine = $"{info.FileName}  ·  {info.SizeLabel}  ·  only in {pack.Name}"
                     + $"  ·  changed {ModifiedLabel(info.LastModified)}",
            CoverageLine = loadsIt ? "loading it" : "not loading it",
            CoverageTip = loadsIt
                ? $"{pack.Name}'s shader config names this pack, so it is the one the game loads."
                : $"In {pack.Name}'s shaderpacks folder but not in its shader config, so the game "
                  + "is not loading it.",
            Tooltip = string.Join("\n", tooltip),
            IsDefault = false,
            HasOwnCopy = false,
            FolderLabel = folder ?? "",
            FolderTip = folder is null ? "" : $"Filed under {folder}",
            IsLocalOnly = info.IsLocal,
            LocalTip = $"In {pack.Name}'s unsynced local/ folder, so nobody else on that instance gets it.",
            OffHere = !loadsIt,
            OffHereTip = $"Not the shader {pack.Name} loads.",
            NeedsLoader = blind,
            LoaderLabel = "NEEDS IRIS",
            LoaderTip = $"{pack.Name} has no Iris, Oculus or OptiFine, so this pack does nothing there "
                      + "at all - the game just renders vanilla and never says why.",
            // Tuned options are worth knowing about before you copy or delete an instance's own pack.
            FormatLabel = info.HasSettings ? "TUNED" : "",
            FormatTip = info.HasSettings
                ? "Iris keeps this pack's tuned options beside it as a .txt, and they travel with the "
                  + "pack when you copy it into another instance."
                : "",
            // No library copy yet; choosing it makes one first (see ToggleRowAsync).
            ToggleOn = false,
            CanToggle = true,
            ToggleTip = $"Make {info.DisplayName} the shader every compatible instance gets and "
                      + $"loads. {pack.Name} keeps the file it has.",
            CanOpen = true
        };
    }

    /// <summary>True when that instance's shader config names this file.</summary>
    private static bool LoadsThis(ShaderModel model, Guid packId, string fileName) =>
        model.Active.TryGetValue(packId, out var active)
        && active is { Length: > 0 }
        && string.Equals(active, fileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True only when the scan found that nothing in that instance can load a shader. An
    /// instance missing from the map is unknown, which doesn't count.</summary>
    private static bool IsBlind(ShaderModel model, Guid packId) =>
        model.Loaders.TryGetValue(packId, out var loader) && loader == ShaderLoader.None;

    private bool IsBlind(Guid packId) => IsBlind(_model, packId);

    /// <summary>What the scan found an instance can load shaders with.</summary>
    /// <remarks>Instances not scanned yet answer <see cref="ShaderLoader.Iris"/>, the same fallback
    /// <see cref="ContentLibraryService.LoaderFor"/> uses, so nothing is hidden on a guess.</remarks>
    private ShaderLoader LoaderOf(PackSummary pack) =>
        _model.Loaders.TryGetValue(pack.Id, out var known) ? known : ShaderLoader.Iris;

    /// <summary>A file's local timestamp in a form <see cref="TimeFormat"/> accepts.</summary>
    /// <remarks>The scanner returns <c>default</c> for a file it couldn't stat, and wrapping a local
    /// <c>DateTime.MinValue</c> throws east of UTC because that instant is before year 1.</remarks>
    private static DateTimeOffset SafeStamp(DateTime local) =>
        local == default ? DateTimeOffset.MinValue : TimeFormat.FromLocal(local);

    private static string ModifiedLabel(DateTime local) =>
        local == default ? "date unknown" : TimeFormat.Date(TimeFormat.FromLocal(local));

    /// <summary>The user folder an item is filed under, or null.</summary>
    private static string? FolderOf(string key)
    {
        foreach (var (name, members) in App.State.Settings.ShaderFolders)
            if (members.Contains(key)) return name;
        return null;
    }

    // ── the scope box ────────────────────────────────────────────────────────

    /// <summary>Builds the instance scope selector, catching its own errors.</summary>
    /// <remarks>It runs inside the refresh's try, whose catch reports a read failure and blanks the
    /// page; a UI bug here shouldn't do that.</remarks>
    private void RebuildScopeBox()
    {
        try { RebuildScopeBoxCore(); }
        catch (Exception ex)
        {
            // Leave whatever the box already had: a stale scope list still lets you pick.
            AppLog.LogError("shaders.scope-box", ex);
        }
    }

    private void RebuildScopeBoxCore()
    {
        using (_filling.Hold())
        {
            var items = new List<ShaderScopeItem> { new(null, "All instances") };
            items.AddRange(_packs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new ShaderScopeItem(p.Id, p.Name)));

            var keep = items.FirstOrDefault(i => i.Id == _scopePackId) ?? items[0];
            _scopePackId = keep.Id;

            PackScopeBox.ItemsSource = items;
            PackScopeBox.DisplayMemberPath = nameof(ShaderScopeItem.Label);
            PackScopeBox.SelectedItem = keep;
        }
        UpdateScopeDependentTips();
    }

    private void OnPackScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling.Busy) return;
        _scopePackId = (PackScopeBox.SelectedItem as ShaderScopeItem)?.Id;
        UpdateScopeDependentTips();
        // The scope changes which packs are listed, so reload rather than refilter.
        _ = LoadAsync();
    }

    /// <summary>Points the "Open folder" tooltip at the instance it will open.</summary>
    private void UpdateScopeDependentTips()
    {
        var pack = ScopedPack() ?? SoleInstance();
        OpenFolderButton.ToolTip = pack is null
            ? "Open an instance's shaderpacks folder - pick which one"
            : $"Open {pack.Name}'s shaderpacks folder";
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
        if (!Enum.TryParse<ShaderSortMode>(tag, out var parsed)) return;
        _sortMode = parsed;
        App.State.Settings.ShaderSortMode = parsed;
        App.State.Settings.Save();
        SyncSortChecks();
        ApplyFilter();
    }

    private void SyncSortChecks()
    {
        SortRecentMenuItem.IsChecked = _sortMode == ShaderSortMode.RecentlyAdded;
        SortNameMenuItem.IsChecked = _sortMode == ShaderSortMode.Name;
        SortSizeMenuItem.IsChecked = _sortMode == ShaderSortMode.Size;
        SortInstanceMenuItem.IsChecked = _sortMode == ShaderSortMode.Instance;

        // Icon-only button, so the tooltip names the current mode.
        SortButton.ToolTip = "Sort shader packs: " + _sortMode switch
        {
            ShaderSortMode.Name => "Name",
            ShaderSortMode.Size => "Size",
            ShaderSortMode.Instance => "Instances using it",
            _ => "Recently added"
        };
    }

    // ── folders ──────────────────────────────────────────────────────────────

    /// <summary>Builds the folder chip strip, catching its own errors.</summary>
    /// <remarks>It runs inside the refresh's try, whose catch reports a read failure and blanks the
    /// page; a UI bug here shouldn't do that.</remarks>
    private void RebuildFolders()
    {
        try { RebuildFoldersCore(); }
        catch (Exception ex)
        {
            AppLog.LogError("shaders.folder-chips", ex);
            _folderChips.Clear();   // an empty strip beats a half-built one
        }
    }

    /// <summary>
    /// Builds the chips: All, every user folder, and the trailing "New folder".
    /// </summary>
    /// <remarks>
    /// A folder is a named member list in <see cref="AppSettings.ShaderFolders"/>, same as the
    /// Instances page strip. An active folder that no longer exists (settings synced from another
    /// machine) falls back to All. <c>AppSettings.ShaderFolderRules</c> is unused here but kept so
    /// stored rules aren't lost.
    /// </remarks>
    private void RebuildFoldersCore()
    {
        // MemberComparer, so the chip count matches the rows the chip filters to.
        var known = _all.Select(r => r.Key).ToHashSet(MemberComparer);
        var chips = new List<ShaderFolderChipVm>
        {
            new("", "All", "", _all.Count, _activeFolder is null, "Every shader pack on this page")
        };

        foreach (var name in App.State.Settings.ShaderFolders.Keys
                     .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
        {
            var count = MemberCount(App.State.Settings.ShaderFolders[name], known);
            chips.Add(new ShaderFolderChipVm(name, name, "", count, _activeFolder == name,
                $"Shader packs you filed under {name}. Folders are a saved view - nothing moves on disk."));
        }

        chips.Add(new ShaderFolderChipVm(NewFolderChipId, "New folder", NewFolderGlyph, 0, false,
            "Create a folder to file shader packs under"));

        // Diffed rather than cleared; a Reset replays the chips' entrance animation on every keystroke.
        ListDiff.Apply(_folderChips, chips, c => c.Id, (kept, fresh) => kept.CopyFrom(fresh));

        if (_activeFolder is { Length: > 0 } active
            && !App.State.Settings.ShaderFolders.ContainsKey(active))
        {
            _activeFolder = null;
            foreach (var chip in _folderChips) chip.SetActive(chip.Id.Length == 0);
        }
    }

    /// <summary>Reserved folder names, checked on create and on rename.</summary>
    /// <remarks>A leading colon marks the strip's pseudo-folders and <c>team:</c> marks team folders in
    /// <see cref="PackListView"/>; a user folder named like either would be mistaken for one.</remarks>
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
        if (App.State.Settings.ShaderFolders.ContainsKey(name))
        {
            Fail("A folder with that name already exists.");
            return;
        }

        App.State.Settings.CreateShaderFolder(name);
        if (addThis is not null) App.State.Settings.AddShaderToFolder(name, addThis.Key);
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
        if (sender is not FrameworkElement { DataContext: ShaderFolderChipVm chip }) return;
        if (chip.Id == NewFolderChipId) { _ = CreateFolderAsync(null); return; }
        _activeFolder = string.IsNullOrEmpty(chip.Id) ? null : chip.Id;
        RebuildFolders();
        ApplyFilter();
    }

    /// <summary>Blocks the context menu on All and New folder; they can't be renamed.</summary>
    private void OnFolderChipRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShaderFolderChipVm chip } && !chip.IsUserFolder)
            e.Handled = true;
    }

    private static ShaderFolderChipVm? FolderFromMenu(object src)
    {
        if (src is not MenuItem leaf) return null;
        DependencyObject? p = leaf;
        while (p is MenuItem m) p = m.Parent as MenuItem ?? m.Parent;
        return p is ContextMenu { PlacementTarget: FrameworkElement fx }
               && fx.DataContext is ShaderFolderChipVm row
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

            // Renames the folder in both settings maps at once, so nothing is left under the old name.
            if (!App.State.Settings.RenameShaderFolder(chip.Id, fresh))
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

            var count = App.State.Settings.ShaderFolders.TryGetValue(chip.Id, out var members)
                ? members.Count : 0;
            if (!await AppDialog.ConfirmAsync(_shell, "Delete folder",
                    $"Delete the folder '{chip.Id}'?"
                    + (count > 0
                        ? $"\n\nThe {count} shader pack(s) filed in it stay exactly where they are - only "
                          + "the grouping goes."
                        : ""),
                    "Delete folder", "Cancel", danger: true))
                return;

            App.State.Settings.DeleteShaderFolder(chip.Id);
            if (_activeFolder == chip.Id) _activeFolder = null;
            App.State.Settings.Save();

            RebuildFolders();
            ApplyFilter();
            Okay($"Deleted the folder '{chip.Id}'.");
        }
        catch (Exception ex) { Fail("That folder could not be deleted.", ex); }
    }

    /// <summary>Removes a deleted pack's key from every folder.</summary>
    private static void ForgetFolderMembership(string key)
    {
        foreach (var members in App.State.Settings.ShaderFolders.Values)
            members.RemoveAll(k => MemberComparer.Equals(k, key));
    }

    // ── folder membership ────────────────────────────────────────────────────

    /// <summary>
    /// The comparer for every "is this filed under that folder" question.
    /// </summary>
    /// <remarks>Case-insensitive because every key carries a file name.
    /// <c>AppSettings.AddShaderToFolder</c> de-dups ordinally, so settings.json can hold two entries
    /// differing only in case; <see cref="SetMember"/> removes every match and
    /// <see cref="MemberCount"/> counts them once.</remarks>
    private static readonly StringComparer MemberComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>The user folder the strip is filtered to, or null for All.</summary>
    private string? ActiveUserFolder() =>
        _activeFolder is { Length: > 0 } folder
        && !IsReservedFolderName(folder)
        && App.State.Settings.ShaderFolders.ContainsKey(folder)
            ? folder
            : null;

    /// <summary>One folder's member keys: the stored list itself, not a copy.</summary>
    private static List<string>? MemberKeys(string? folder) =>
        folder is { Length: > 0 }
        && App.State.Settings.ShaderFolders.TryGetValue(folder, out var list)
            ? list
            : null;

    /// <summary>Is this key filed under that folder?</summary>
    private static bool IsMember(IReadOnlyList<string>? members, string key) =>
        members is not null && members.Any(k => MemberComparer.Equals(k, key));

    /// <summary>Files a key under a folder, or takes it out, and saves right away (unlike the
    /// <see cref="AppSettings"/> mutators it calls), since filing is a single menu click.</summary>
    private static void SetMember(string folder, string key, bool wanted)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(key)) return;
        if (wanted)
        {
            if (!IsMember(MemberKeys(folder), key)) App.State.Settings.AddShaderToFolder(folder, key);
        }
        else if (MemberKeys(folder) is { } list)
        {
            // Every match, not just the first: see MemberComparer.
            list.RemoveAll(k => MemberComparer.Equals(k, key));
        }
        App.State.Settings.Save();
    }

    /// <summary>The number on a folder's chip: how many of its members this page has a row for.</summary>
    /// <remarks>Distinct, with the filter's comparer, so two spellings count once.</remarks>
    private static int MemberCount(IReadOnlyList<string> members, HashSet<string> known) =>
        members.Distinct(MemberComparer).Count(known.Contains);

    /// <summary>
    /// Files this pack under a folder, or takes it out.
    /// </summary>
    /// <remarks>Only settings.json changes, so there is no reload; <see cref="ApplyFilter"/> recounts the
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

    /// <summary>
    /// Narrows <see cref="_all"/> into the list the user sees, and writes the page's count.
    /// </summary>
    /// <remarks>Diffed into the bound collection (<see cref="ListDiff"/>) rather than assigned, which
    /// would reset the scroll position, selection and entrance animation on every keystroke.</remarks>
    private void ApplyFilter(bool announce = false)
    {
        RebuildFolders();
        UpdateLoaderWarning();

        IEnumerable<ContentRow> seq = _all;
        var filtered = false;

        // The folder chip shows only the rows filed in it, as on the Instances page.
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

        // Only the load path sets the page state; a keystroke mid-scan must not replace "Scanning..."
        // with a count from a half-filled list.
        if (!announce && _state.Kind is PageStateKind.Loading or PageStateKind.Refreshing) return;

        if (PackList.Rows.Count == 0 && filtered && _all.Count > 0)
        {
            // Tell the empty panel whether nothing is here or nothing matched; they need different actions.
            if (folder is { Length: > 0 } active && query.Length == 0)
                _state.EmptyNext($"Nothing in {active} yet",
                    "Right-click a shader pack on the All chip and pick 'Add to folder' to file it "
                    + "here. Nothing moves on disk - a folder is a saved view.",
                    glyph: "");
            else
                _state.EmptyFiltered();
        }

        _state.Content(PackList.Rows.Count, note: _cacheNote ?? _staleNote);
    }

    private ShaderFacts FactsFor(ContentRow row) =>
        _model.Facts.TryGetValue(row.Key, out var facts)
            ? facts
            : new ShaderFacts(0, 0, 0, 0, row.Item?.SizeBytes ?? 0,
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
            ShaderSortMode.Name =>
                rows.OrderByDescending(r => r.IsDefault)
                    .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            ShaderSortMode.Size =>
                rows.OrderByDescending(r => r.IsDefault)
                    .ThenByDescending(r => FactsFor(r).SizeBytes)
                    .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            ShaderSortMode.Instance =>
                rows.OrderByDescending(r => r.IsDefault)
                    .ThenByDescending(r => FactsFor(r).Present)
                    .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ =>
                rows.OrderByDescending(r => r.IsDefault)
                    .ThenByDescending(r => FactsFor(r).Modified)
                    .ThenBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };

    // ── the loader banner ────────────────────────────────────────────────────

    /// <summary>
    /// Warns when an instance can't load shader packs at all.
    /// </summary>
    /// <remarks>Without Iris, Oculus or OptiFine the game just renders vanilla with no hint why. Scoped,
    /// the banner is about that instance; on "All instances" it names the first instance holding packs
    /// with no loader. It stays hidden while the loader map is empty (the cached paint), since unknown
    /// is not "none".</remarks>
    private void UpdateLoaderWarning()
    {
        if (_model.Loaders.Count == 0)
        {
            LoaderWarning.Visibility = Visibility.Collapsed;
            LoaderWarning.Tag = null;
            return;
        }

        // No instance has a loader, so nothing on this page can do anything anywhere yet.
        if (_model.Loaders.Values.All(l => l == ShaderLoader.None))
        {
            // Prefer the scoped instance so "Open mods" opens the one the user is looking at.
            LoaderWarning.Tag = ScopedPack()?.Id ?? _model.Loaders.Keys.First();
            LoaderWarningText.Text =
                "No instance can load a shader pack yet - none of them has Iris, Oculus or OptiFine. "
                + "Whatever you choose here is stored and will do nothing in game until one of them "
                + "does; the launcher never switches a shader on where nothing can read it.";
            LoaderWarning.Visibility = Visibility.Visible;
            return;
        }

        Guid? blind = null;
        if (ScopedPack() is { } scope)
        {
            if (IsBlind(scope.Id)) blind = scope.Id;
        }
        else
        {
            foreach (var candidate in _model.Installed.Select(i => i.Pack.Id).Distinct())
            {
                if (!IsBlind(candidate)) continue;
                blind = candidate;
                break;
            }
        }

        if (blind is null)
        {
            LoaderWarning.Visibility = Visibility.Collapsed;
            LoaderWarning.Tag = null;
            return;
        }

        LoaderWarning.Tag = blind;
        LoaderWarningText.Text =
            $"{NameOf(blind)} has no shader loader installed - a shader pack there does nothing until "
            + "Iris (Fabric/NeoForge) or Oculus (Forge) is added to its mods. Nothing is switched on "
            + "there, by you or by the launcher, until then.";
        LoaderWarning.Visibility = Visibility.Visible;
    }

    private async void OnOpenModsForLoader(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LoaderWarning.Tag is not Guid packId) return;
            var detail = await App.State.Api.GetPackAsync(packId);
            _shell.OpenModExplorerForPack(detail);
        }
        catch (Exception ex) { Fail("That instance's mods could not be opened.", ex); }
    }

    private string NameOf(Guid? packId) =>
        packId is null ? "that instance" : _packs.FirstOrDefault(p => p.Id == packId)?.Name ?? "that instance";

    // ── header actions ───────────────────────────────────────────────────────

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        try { await LoadAsync(force: true); }
        catch (Exception ex) { Fail("The shaderpacks folders could not be re-read.", ex); }
    }

    private void OnBrowse(object sender, RoutedEventArgs e) => OpenBrowser();

    /// <summary>Opens the store in the side panel. The browser downloads and notifies this page, which
    /// renders the result.</summary>
    private void OpenBrowser()
    {
        if (_browser is not null) _browser.Installed -= OnBrowserInstalled;
        _browser = _shell.OpenShaderBrowser(_packs, ScopedPack()?.Id ?? SoleInstance()?.Id);
        _browser.Installed += OnBrowserInstalled;
    }

    private async void OnBrowserInstalled(Guid packId)
    {
        try
        {
            Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(packId));
            await LoadAsync(force: true);
            Okay($"Installed into {NameOf(packId)}. Choose it to hand it to every compatible instance.");
        }
        catch (Exception ex) { Fail("The new pack could not be listed.", ex); }
    }

    /// <summary>
    /// Import: pick the file, pick instances (or none), optionally publish it. Handled by
    /// <see cref="ImportContentCard"/>, shared with the other content pages.
    /// </summary>
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try { await RunImportAsync(null); }
        catch (Exception ex) { Fail("That import did not finish.", ex); }
    }

    private async Task RunImportAsync(string? prePickedPath)
    {
        var outcome = await ImportContentCard.ShowAsync(_shell, new ImportRequest(
            ImportContentKind.ShaderPack, _packs,
            PreferredPackId: ScopedPack()?.Id ?? SoleInstance()?.Id,
            PrePickedPath: prePickedPath));
        if (outcome is null) return;

        foreach (var target in outcome.Targets) Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(target));
        await LoadAsync(force: true);
        Okay(outcome.Summary);
    }

    /// <summary>Opens an instance's <c>shaderpacks</c> folder: the scoped one, the only one, or one the
    /// user picks.</summary>
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var pack = ScopedPack() ?? SoleInstance();
        if (pack is not null) { OpenShadersFolder(pack); return; }
        if (_packs.Count == 0)
        {
            Fail("There is no instance to open yet.");
            return;
        }

        var menu = new ContextMenu { PlacementTarget = OpenFolderButton, Placement = PlacementMode.Bottom };
        foreach (var candidate in _packs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var target = candidate;
            menu.Items.Add(ContentMenu.Item($"Shader packs in {target.Name}", () => OpenShadersFolder(target)));
        }
        menu.IsOpen = true;
    }

    /// <summary>Creates the folder if needed, then opens it.</summary>
    private void OpenShadersFolder(PackSummary pack)
    {
        try
        {
            var dir = App.State.Shaders.FolderFor(pack.Id);
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) Fail("That folder could not be opened.");
        }
        catch (Exception ex) { Fail("That folder could not be opened.", ex); }
    }

    // ── row callbacks ────────────────────────────────────────────────────────

    /// <summary>Opens the pack's own page, on every row the launcher has anything for.</summary>
    /// <remarks>An instance's copy is preferred, since it can tell whether that instance's shader
    /// config names the pack.</remarks>
    private void OpenRow(ContentRow row)
    {
        if (Installed(row) is { } installed)
        {
            _shell.OpenShaderPackDetail(installed.Info.Key, row.DisplayName);
            return;
        }
        if (row.Item is { } item)
        {
            _shell.OpenLibraryShaderPackDetail(item, row.DisplayName);
            return;
        }
        // A stale row the model no longer lists; just reveal the file.
        Reveal(row);
    }

    /// <summary>The instance copy behind a row: the row's own file, or the library item's copy in the
    /// scoped instance.</summary>
    private ShaderInstalled? Installed(ContentRow row)
    {
        if (row.Item is null)
            return _model.Installed.FirstOrDefault(i => i.Info.Key == row.Key);

        var scope = _scopePackId;
        return _model.Installed
            .Where(i => string.Equals(i.Info.FileName, row.Item.FileName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => scope is Guid id && i.Pack.Id == id)
            .FirstOrDefault(i => SameFile(row.Item.Path, i.Info.FilePath, cheap: false));
    }

    /// <summary>True when this file in this instance is the library's copy rather than the instance's
    /// own.</summary>
    /// <remarks>Decides whether a removal goes through <see cref="ContentLibraryService.UnapplyAsync"/>
    /// or <see cref="ShaderPackService.DeleteAsync"/>. Compares content, not names: an instance's own
    /// same-named pack is a different file.</remarks>
    private bool IsLibraryCopy(ShaderInstalled installed) =>
        _model.Library.Any(i =>
            string.Equals(i.FileName, installed.Info.FileName, StringComparison.OrdinalIgnoreCase)
            && SameFile(i.Path, installed.Info.FilePath, cheap: false));

    /// <summary>
    /// The row's selection circle: is this the shader pack you want everywhere.
    /// </summary>
    /// <remarks>A row with no library copy is promoted first. Pointing one instance at a pack is on the
    /// row's menu instead.</remarks>
    private async Task ToggleRowAsync(ContentRow row, bool wanted)
    {
        try
        {
            if (row.RowClass == ContentRowClass.Library) { await SetChosenAsync(row, wanted); return; }

            // A row without a library copy is never on, so an off here is a row rebuilt mid-click.
            if (!wanted) return;
            if (await PromoteAsync(row) is not { } item) return;
            // The promote reloaded the page, so look up the new row keyed on the library copy.
            if (RowFor(item.Key) is { } promoted) await SetChosenAsync(promoted, true);
        }
        catch (Exception ex) { Fail("That shader pack could not be switched.", ex); }
    }

    /// <summary>The row now carrying an item's key, after a reload has rebuilt the list.</summary>
    private ContentRow? RowFor(string key) =>
        _all.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// Makes this the shader pack every compatible instance gets and loads, or stops offering one.
    /// </summary>
    /// <remarks>An instance has one shader slot, so choosing a pack unchooses the previous one.
    /// Activation goes with it; <see cref="ContentActivationService.TrySetShader"/> still refuses where
    /// no loader can read it. Turning it off never removes copies already placed.</remarks>
    private async Task SetChosenAsync(ContentRow row, bool wanted)
    {
        try
        {
            if (row.Item is not { } item) return;

            // The previous choice. Read from the model, since it may have no row on screen.
            var previous = _model.Library.FirstOrDefault(i =>
                i.Defaults is { Enabled: true }
                && !string.Equals(i.Key, item.Key, StringComparison.Ordinal));

            if (wanted && previous is not null)
            {
                var off = App.State.Library.GetDefaults(previous.Key);
                off.Enabled = false;
                off.Activate = false;
                App.State.Library.SetDefaults(previous.Key, off);
            }

            var policy = App.State.Library.GetDefaults(item.Key);
            policy.Enabled = wanted;
            policy.Activate = wanted;

            // Keep it on this machine: shaderpacks/ syncs on shared instances, and one click shouldn't push
            // a personal pack to every collaborator.
            var keepLocal = policy.KeepLocal || wanted;
            policy.KeepLocal = keepLocal;
            App.State.Library.SetDefaults(item.Key, policy);
            if (keepLocal) App.State.Library.SetKeepLocal(item.Key, true);

            await LoadAsync(force: true);
            // No instance copy yet: copies arrive when each instance next starts.
            Okay(wanted
                ? $"{row.DisplayName} is the shader every compatible instance gets, kept on this machine"
                  + (previous is not null ? $" - {previous.DisplayName} gave up the slot." : ".")
                  + (Installed(row) is null
                      ? " Each instance takes a copy the next time it starts - open its page after that "
                        + "to untick any of them."
                      : " Open its page to untick the instances you do not want it in.")
                : $"No shader pack is being handed out now. Copies already in your instances stay where "
                  + "they are, and each one carries on loading whatever it loads.");
        }
        catch (Exception ex) { Fail("That shader pack could not be switched.", ex); }
    }

    // ── the row menu ─────────────────────────────────────────────────────────

    private const int IcOpen = 0xE7C3, IcDefault = 0xE73E, IcOn = 0xE706;
    private const int IcRename = 0xE8AC, IcReveal = 0xE8DA, IcFolder = 0xE8F1, IcShare = 0xE8EC;
    private const int IcCopy = 0xE8C8, IcLocal = 0xE753, IcDelete = 0xE74D;
    private const int IcMore = 0xE712, IcApply = 0xE895, IcUpdate = 0xE895, IcSettings = 0xE713;

    /// <summary>
    /// The row's menu, built when it is opened.
    /// </summary>
    /// <remarks>Left-click on "..." gives the short list, right-click the full one. Built on demand
    /// because a large <c>ContextMenu</c> in the item template would be built for every row.</remarks>
    private ContextMenu? BuildRowMenu(ContentRow row, bool full)
    {
        var scope = ScopedPack();
        var installed = Installed(row);
        var menu = ContentMenu.New(row.DisplayName,
            row.Item is null
                ? $"only in {installed?.Pack.Name ?? "one instance"}"
                : row.IsDefault
                    ? "the shader every compatible instance gets"
                    : "off - put in by hand");

        // Every row with an instance copy or a library item has a page.
        if (installed is not null || row.Item is not null)
            menu.Items.Add(ContentMenu.Item("Open shader page", () => OpenRow(row),
                ContentMenu.Glyph(IcOpen)));

        // The row's circle as a menu item. Works without a library copy too (ToggleRowAsync promotes
        // first).
        menu.Items.Add(ContentMenu.Toggle("The one I want everywhere", row.IsDefault, () =>
        {
            _ = ToggleRowAsync(row, !row.IsDefault);
            return !row.IsDefault;
        }, ContentMenu.Glyph(IcDefault)));

        // The per-instance slot needs one instance: the scope, or the instance an un-libraried row
        // belongs to.
        if (installed is not null && (scope is null || installed.Pack.Id == scope.Id))
        {
            var loadsIt = LoadsThis(_model, installed.Pack.Id, installed.Info.FileName);
            menu.Items.Add(ContentMenu.Item(
                loadsIt ? $"Stop {installed.Pack.Name} loading a shader" : $"Use it in {installed.Pack.Name}",
                () => _ = SetActiveAsync(installed, !loadsIt), ContentMenu.Glyph(IcOn)));
        }

        // "Add to folder" is in both the short and the full menu, as on the Instances page.
        menu.Items.Add(BuildFolderMenu(row));

        if (!full)
        {
            // A library row can always reveal its own copy, even before any instance has one.
            if (row.Path is { Length: > 0 })
                menu.Items.Add(ContentMenu.Item("Reveal in Explorer", () => Reveal(row), ContentMenu.Glyph(IcReveal)));
            menu.Items.Add(new Separator());
            menu.Items.Add(ContentMenu.Item("More options...",
                () => ContentMenu.ShowInstead(menu, () => BuildRowMenu(row, full: true)!),
                ContentMenu.Glyph(IcMore)));
            return menu;
        }

        // ── everything ──────────────────────────────────────────────────────
        menu.Items.Add(new Separator());

        if (row.Item is { } libraryItem)
        {
            // The reconciler can't place a single item, so this places everything that is on.
            menu.Items.Add(ContentMenu.Item("Place everything that is on, including this",
                () => _ = ApplyOneAsync(row), ContentMenu.Glyph(IcApply),
                gesture: row.IsDefault ? null : "switched off", enabled: row.IsDefault));
            menu.Items.Add(BuildInstancesMenu(row, libraryItem));
            menu.Items.Add(ContentMenu.Toggle("Keep on this machine", libraryItem.KeepLocal, () =>
            {
                var wanted = !libraryItem.KeepLocal;
                App.State.Library.SetKeepLocal(libraryItem.Key, wanted);
                // Report after the reload, which resets the status line.
                _ = ReloadThenSayAsync(wanted
                    ? $"{row.DisplayName} will never be uploaded from a shared instance."
                    : $"{row.DisplayName} will be uploaded with any shared instance it is in.");
                return wanted;
            }, ContentMenu.Glyph(IcLocal)));
        }

        menu.Items.Add(ContentMenu.Item("Rename...", () => _ = RenameAsync(row), ContentMenu.Glyph(IcRename),
            gesture: "F2"));

        if (installed is not null)
        {
            // Iris keeps tuned options beside each instance's copy. They're never shared, so these items
            // are about one instance.
            menu.Items.Add(ContentMenu.Item($"Open its settings in {installed.Pack.Name}",
                () => OpenSettingsFile(installed), ContentMenu.Glyph(IcSettings),
                gesture: installed.Info.HasSettings ? null : "none yet",
                enabled: installed.Info.HasSettings));
            menu.Items.Add(ContentMenu.Item("Reset its tuned settings...",
                () => _ = ResetSettingsAsync(installed), enabled: installed.Info.HasSettings));

            // Only for an instance's own pack: updating a library link in place would change the file for
            // every instance sharing it.
            if (row.Item is null && installed.Info.HasProvenance)
                menu.Items.Add(ContentMenu.Item("Update...", () => _ = UpdateAsync(installed),
                    ContentMenu.Glyph(IcUpdate)));

            menu.Items.Add(ContentMenu.Item("Copy to another instance...", () => _ = CopyElsewhereAsync(installed),
                ContentMenu.Glyph(IcCopy)));
        }

        menu.Items.Add(ContentMenu.Item("Reveal in Explorer", () => Reveal(row), ContentMenu.Glyph(IcReveal)));
        menu.Items.Add(ContentMenu.Item("Copy key", () =>
        {
            if (ClipboardHelper.TrySetText(row.Key)) Okay("Copied key.");
        }));

        menu.Items.Add(new Separator());
        if (installed is not null)
            menu.Items.Add(ContentMenu.Item(
                IsLibraryCopy(installed)
                    ? $"Take it out of {installed.Pack.Name}..."
                    : $"Delete from {installed.Pack.Name}...",
                () => _ = RemoveFromInstanceAsync(row, installed), ContentMenu.Glyph(IcDelete)));
        if (row.Item is not null)
            menu.Items.Add(ContentMenu.Item("Stop handing this out...", () => _ = RemoveFromLibraryAsync(row),
                ContentMenu.Glyph(IcDelete)));

        return menu;
    }

    /// <summary>The per-instance submenu: who has it, who could, and what is in the way.</summary>
    /// <remarks>Built on open since every entry reads the disk. This is a one-off "put it there now";
    /// which instances the pack is for is set on its own page.</remarks>
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
                AppLog.LogError(nameof(ShaderPacksView), ex);
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

            if (states.Any(s => s.HasOwnCopy))
                parent.Items.Add(ContentMenu.Item("Replace the instances' own copies...",
                    () => _ = ReplaceOwnAsync(item, states)));

            if (!item.IsFolder && states.Any(s => s.Diverged))
                parent.Items.Add(ContentMenu.Item("Re-link the instances that drifted",
                    () => _ = RelinkAsync(item, states)));
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
            foreach (var name in App.State.Settings.ShaderFolders.Keys
                         .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
            {
                var folder = name;
                // Same helper as the chip count, so the menu and the count agree.
                var inside = IsMember(MemberKeys(folder), row.Key);
                parent.Items.Add(ContentMenu.Toggle(folder, inside, () =>
                {
                    FileIntoFolder(row, folder, !inside);
                    return !inside;
                }));
            }
            // Matches the Instances page's "Add to folder" (PackListView.OnCtxAddToFolderOpened).
            if (App.State.Settings.ShaderFolders.Count == 0)
                parent.Items.Add(ContentMenu.Item("(no folders - create one first)", () => { },
                    enabled: false));

            parent.Items.Add(new Separator());
            parent.Items.Add(ContentMenu.Item("Create new folder...", () => _ = CreateFolderAsync(row)));
        };
        parent.Items.Add(ContentMenu.Item("(reading...)", () => { }, enabled: false));
        return parent;
    }

    // ── row actions ──────────────────────────────────────────────────────────

    /// <summary>
    /// Copies a pack that is only in one instance into the library, leaving that instance on the same
    /// bytes.
    /// </summary>
    /// <remarks>The only promote path on this page. It goes through
    /// <see cref="ContentDefaultsService.AddFromInstanceAsync"/>, which handles two packs sharing a file
    /// name, and doesn't touch the shader slot. Returns the new library item, or null. The page has been
    /// reloaded by then, so callers look up the new row by key.</remarks>
    private async Task<LibraryItem?> PromoteAsync(ContentRow row)
    {
        try
        {
            if (Installed(row) is not { } installed)
            {
                Fail("That shader pack is no longer where it was - try Refresh.");
                return null;
            }

            // Only mention copying when the drive can't hard-link and the file really is duplicated.
            _state.Begin(App.State.Library.SupportsHardLinks
                ? $"Choosing {installed.Info.DisplayName}..."
                : $"Choosing {installed.Info.DisplayName} - copying it, because this drive cannot "
                  + "share one file between instances...",
                refreshing: true);
            var result = await App.State.ContentDefaults.AddFromInstanceAsync(
                installed.Pack,
                [new ContentAddRequest(installed.Info.FilePath, LibraryKind.ShaderPack,
                    installed.Info.DisplayName)],
                makeDefault: false, policyTemplate: null,
                new Progress<string>(_state.Progress), _scanCts?.Token ?? default);

            Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(installed.Pack.Id));
            await LoadAsync(force: true);
            // Refused (the game is running there): nothing was attempted, so report a failure.
            if (result.Refused is { Length: > 0 } refused) { Fail(refused); return null; }
            Okay(result.Summary());
            return result.Items.FirstOrDefault()?.Item;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Fail("That shader pack could not be chosen.", ex); return null; }
    }

    /// <summary>Runs the reconciler now, which places everything that is switched on, this row
    /// included.</summary>
    /// <remarks>There is no single-item reconcile: plans are built per instance over the whole library,
    /// since the one shader slot only makes sense with everything considered.</remarks>
    private async Task ApplyOneAsync(ContentRow row)
    {
        if (row.Item is null) return;
        try
        {
            _state.Begin($"Placing everything that is on, {row.DisplayName} included...", refreshing: true);
            var result = await App.State.ContentDefaults.ReconcileAllAsync(
                _packs, activate: true, new Progress<string>(_state.Progress), _scanCts?.Token ?? default);
            foreach (var pack in _packs) Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(pack.Id));
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
            foreach (var target in targets) Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(target.Id));
            await LoadAsync(force: true);
            // Placing a file doesn't activate it; each instance keeps loading what it was loading.
            Okay($"{result.Summary()}  ·  nothing was switched on - each instance still loads whatever it "
               + "was loading.");
        }
        catch (Exception ex) { Fail("That shader pack could not be shared.", ex); }
    }

    private async Task UnapplyAsync(LibraryItem item, IReadOnlyList<PackSummary> targets)
    {
        try
        {
            var result = await App.State.Library.UnapplyAsync([item], targets);
            foreach (var target in targets) Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(target.Id));
            await LoadAsync(force: true);
            Okay(result.Summary());
        }
        catch (Exception ex) { Fail("That shader pack could not be taken out of the instance.", ex); }
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
            foreach (var target in drifted) Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(target.Id));
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
                    + "long as any instance still holds it, and an instance already loading it carries on "
                    + "loading it. Nothing will place it in a new instance again.",
                    "Stop handing it out", "Cancel", danger: true))
                return;

            await App.State.Library.RemoveAsync(item.Key);
            ForgetFolderMembership(item.Key);
            App.State.Settings.Save();
            await LoadAsync(force: true);
            Okay($"{item.DisplayName} is no longer handed to your instances.");
        }
        catch (Exception ex) { Fail("That item could not be removed.", ex); }
    }

    /// <summary>
    /// Takes one shader pack out of one instance: through the library when it is the library's copy,
    /// otherwise through <see cref="ShaderPackService.DeleteAsync"/>.
    /// </summary>
    /// <remarks>Routed by content, not by which menu item was clicked.
    /// <see cref="ShaderPackService.Delete"/> also deletes the twin on the other side of the launch
    /// overlay and the pack's settings, which is wrong for a library link;
    /// <see cref="ContentLibraryService.UnapplyAsync"/> only matches the library file.</remarks>
    private async Task RemoveFromInstanceAsync(ContentRow row, ShaderInstalled installed)
    {
        try
        {
            var info = installed.Info;
            var loadsIt = LoadsThis(_model, installed.Pack.Id, info.FileName);

            if (IsLibraryCopy(installed))
            {
                var item = row.Item ?? _model.Library.FirstOrDefault(i =>
                    string.Equals(i.FileName, info.FileName, StringComparison.OrdinalIgnoreCase)
                    && SameFile(i.Path, info.FilePath, cheap: false));
                if (item is null) { Fail("That shader pack could not be found - try Refresh."); return; }

                if (!await AppDialog.ConfirmAsync(_shell, "Take it out of this instance",
                        $"Remove {item.DisplayName} from {installed.Pack.Name}?\n\n"
                        + "The pack itself stays and every other instance using it carries on working "
                        + "- only this instance's link goes."
                        + (loadsIt
                            ? $"\n\n{installed.Pack.Name} is set to load it, so it will fall back to no shader "
                              + "at all until you pick another."
                            : "")
                        + (row.IsDefault
                            ? "\n\nIt is the pack you want everywhere, so untick this instance on its own "
                              + "page or the next reconcile puts it straight back."
                            : ""),
                        "Take it out", "Cancel", danger: true))
                    return;

                await UnapplyAsync(item, [installed.Pack]);
                return;
            }

            var what = info.IsFolder ? "shader folder" : "zip";
            if (!await AppDialog.ConfirmAsync(_shell, "Delete shader pack",
                    $"Permanently delete the {what} '{info.FileName}' from {installed.Pack.Name}?\n\n"
                    + "This is that instance's own copy, and it goes from disk along with any settings you "
                    + "tuned for it."
                    + (loadsIt ? " It is the pack that instance loads, so shaders there are switched off." : ""),
                    "Delete", "Cancel", danger: true))
                return;

            await App.State.Shaders.DeleteAsync(info);
            ForgetFolderMembership(info.Key);
            App.State.Settings.Save();
            Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(installed.Pack.Id));
            await LoadAsync(force: true);
            Okay($"Deleted {info.FileName} from {installed.Pack.Name}.");
        }
        catch (Exception ex) { Fail("That shader pack could not be removed.", ex); }
    }

    /// <summary>
    /// Points one instance's shader loader at this pack, or switches shaders off there.
    /// </summary>
    /// <remarks>Asks first when the instance has no loader (nothing would read the config) or when the
    /// game is running (the change waits for a restart and may be overwritten).</remarks>
    private async Task SetActiveAsync(ShaderInstalled installed, bool wanted)
    {
        try
        {
            var pack = installed.Pack;
            var info = installed.Info;

            if (wanted && IsBlind(pack.Id))
            {
                if (!await AppDialog.ConfirmAsync(_shell, "Nothing here can load a shader pack",
                        $"{pack.Name} has no Iris, Oculus or OptiFine, so nothing there reads a shader "
                        + "config.\n\nThe launcher can still write the setting - it will start working the "
                        + "moment you install Iris (Fabric/NeoForge) or Oculus (Forge) - but until then the "
                        + "game renders vanilla and never says why.",
                        "Set it anyway", "Cancel"))
                    return;
            }

            if (App.State.Instances.IsBusy(pack.Id)
                && !await AppDialog.ConfirmAsync(_shell, "Minecraft is running",
                        $"{pack.Name} is open. The shader loader reads its config when the game starts and "
                        + "writes to it when you change shaders in game, so this change may not apply until "
                        + "a restart and may be written over.\n\nDo it anyway?",
                        "Do it anyway", "Cancel", danger: true))
                return;

            App.State.Shaders.SetActiveShader(pack.Id, wanted ? info.FileName : null);

            Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(pack.Id));
            await LoadAsync(force: true);
            Okay(wanted
                ? $"{info.DisplayName} is now the shader pack {pack.Name} loads. It applies the next time "
                  + "that instance starts."
                : $"Shaders are off for {pack.Name}.");
        }
        catch (Exception ex) { Fail("The shader could not be changed.", ex); }
    }

    /// <summary>Renames the label the launcher shows. The file keeps its name, since the loader's config
    /// points at it.</summary>
    private async Task RenameAsync(ContentRow row)
    {
        try
        {
            var name = (await _shell.PromptAsync("Rename shader pack", "Display name", row.DisplayName))?.Trim();
            if (string.IsNullOrEmpty(name)) return;

            if (row.Item is { } item) App.State.Library.Rename(item.Key, name);
            else
            {
                App.State.Shaders.Rename(row.Key, name);
                if (row.PackId is Guid packId) Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(packId));
            }
            await LoadAsync(force: true);
            Okay($"Renamed to {name}.");
        }
        catch (Exception ex) { Fail("That shader pack could not be renamed.", ex); }
    }

    private void Reveal(ContentRow row)
    {
        try
        {
            var path = row.Path ?? Installed(row)?.Info.FilePath;
            if (path is null || (!File.Exists(path) && !Directory.Exists(path)))
            {
                Fail("That file is no longer there.");
                return;
            }
            if (!SafeLaunch.RevealFile(path)) Fail("Explorer could not be opened.");
        }
        catch (Exception ex) { Fail("Explorer could not be opened.", ex); }
    }

    private void OpenSettingsFile(ShaderInstalled installed)
    {
        var info = installed.Info;
        if (!info.HasSettings || info.SettingsPath is not { Length: > 0 } path)
        {
            Okay($"{info.DisplayName} has no saved settings yet - tune it in game once and Iris writes "
               + "them beside the pack.");
            return;
        }
        FileEditorWindow.OpenFileFor(_shell, installed.Pack.Id, installed.Pack.Name, path);
    }

    private async Task ResetSettingsAsync(ShaderInstalled installed)
    {
        try
        {
            var info = installed.Info;
            if (!info.HasSettings)
            {
                Okay($"{info.DisplayName} is already at the author's defaults.");
                return;
            }
            if (!await AppDialog.ConfirmAsync(_shell, "Reset shader settings",
                    $"Throw away the settings you tuned for {info.DisplayName} in {installed.Pack.Name}?"
                    + "\n\nThe pack itself stays; it goes back to the author's defaults.",
                    "Reset", "Cancel", danger: true))
                return;

            var done = await Task.Run(() => App.State.Shaders.ResetSettings(info));
            Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(installed.Pack.Id));
            await LoadAsync(force: true);
            Okay(done ? $"Reset {info.DisplayName} to its defaults." : "There were no settings to reset.");
        }
        catch (Exception ex) { Fail("Those settings could not be reset.", ex); }
    }

    /// <summary>Copies one instance's pack into another instance.</summary>
    /// <remarks>An independent copy. Sharing one file goes through the library ("Put it in...").</remarks>
    private async Task CopyElsewhereAsync(ShaderInstalled installed)
    {
        try
        {
            var targets = _packs.Where(p => p.Id != installed.Pack.Id).ToList();
            if (targets.Count == 0) { Fail("There is no other instance to copy it into."); return; }

            var pick = new PackPickerDialog(targets, "Copy shader pack",
                $"Pick the instance to copy {installed.Info.DisplayName} into.", "Copy") { Owner = _shell };
            if (pick.ShowDialog() != true || pick.SelectedPackId is not Guid target) return;

            var pack = targets.First(p => p.Id == target);
            if (App.State.Shaders.Exists(pack.Id, installed.Info.FileName, includeLocal: true))
            {
                Okay($"{pack.Name} already has a shader pack called {installed.Info.FileName}.");
                return;
            }

            _state.Begin($"Copying into {pack.Name}...", refreshing: true);
            await App.State.Shaders.CopyToAsync(installed.Info, pack.Id);
            Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(pack.Id));
            await LoadAsync(force: true);
            Okay($"Copied {installed.Info.DisplayName} into {pack.Name} - its tuned settings came across, "
               + "and nothing there was switched on.");
        }
        catch (Exception ex) { Fail("That shader pack could not be copied.", ex); }
    }

    // ── updating an installed shader ─────────────────────────────────────────

    /// <summary>
    /// Offers the store's other versions of an installed shader, and swaps the file for the chosen one.
    /// </summary>
    /// <remarks>Only for a pack the launcher installed (so the project is known) and only for an
    /// instance's own copy, since swapping a library link would change it for every instance. Tuned Iris
    /// settings are carried over, and the slot is re-pointed if this pack was active.</remarks>
    private async Task UpdateAsync(ShaderInstalled installed)
    {
        try
        {
            var shader = installed.Info;
            if (!shader.HasProvenance || shader.ProjectId is not { Length: > 0 } projectId
                || shader.Source is not { } source)
            {
                Okay($"{shader.DisplayName} was not installed from a store, so there is nothing to update "
                   + "it from. Import it again and the launcher will remember where it came from.");
                return;
            }

            _state.Note($"Looking for newer versions of {shader.DisplayName}...");

            var project = new ModSummary(projectId, "", shader.DisplayName, null, null, 0, null, source,
                Array.Empty<string>());
            var versions = (await App.State.ModVersions.GetVersionsAsync(project)).ToList();
            if (versions.Count == 0) { Okay("That store lists no versions for this pack any more."); return; }

            var pick = await ModVersionPickerDialog.ShowAsync(_shell, shader.DisplayName, versions,
                installed.Pack.MinecraftVersion, null, shader.VersionId, shader.VersionNumber);
            if (pick is null) { _state.Note(""); return; }

            var file = pick.Version.Files.FirstOrDefault(f => f.IsPrimary) ?? pick.Version.Files.FirstOrDefault();
            var url = file?.DownloadUrl;
            if (string.IsNullOrWhiteSpace(url) && pick.Version.Source == ModSource.CurseForge)
            {
                var ids = pick.Version.Id.Split(':');
                if (ids.Length == 2 && int.TryParse(ids[0], out var modId) && int.TryParse(ids[1], out var fileId))
                    url = await App.State.CurseForge.GetDownloadUrlAsync(modId, fileId);
            }
            if (string.IsNullOrWhiteSpace(url) || file is null)
            {
                Okay("That version has no download link - get it from the project's site instead.");
                return;
            }

            _workCts?.Cancel();
            _workCts = new CancellationTokenSource();
            var ct = _workCts.Token;

            var fileName = Path.GetFileName(file.Filename);
            if (!PathSafety.IsSafeFileName(fileName))
            {
                AppLog.Log(nameof(ShaderPacksView), $"Skipped an update of {shader.DisplayName}: the store's file name is not a plain name: {file.Filename}");
                Fail("Update failed: the store gave a file name that cannot be used.");
                return;
            }
            var dir = App.State.Shaders.FolderFor(shader.SourcePackId);
            Directory.CreateDirectory(dir);
            var dest = ShaderPackService.NextFreePath(dir, fileName);
            var replacingSelf = string.Equals(fileName, shader.FileName, StringComparison.OrdinalIgnoreCase);
            var wasLoaded = LoadsThis(_model, installed.Pack.Id, shader.FileName);

            // Shader zips run 5-60 MB, so show progress and allow cancel. Begin as a refresh keeps the list
            // on screen.
            var label = $"Downloading {shader.DisplayName} {pick.Version.VersionNumber}...";
            _state.Begin(label, refreshing: true);
            _state.Progress(label);
            var progress = new Progress<(long done, long total)>(p =>
                _state.Progress(p.total > 0
                    ? $"{label}  {p.done / 1024 / 1024.0:0.#} / {p.total / 1024 / 1024.0:0.#} MB"
                    : label));

            await App.State.Modrinth.DownloadFileAsync(url!, dest, progress, ct);

            var installedName = Path.GetFileName(dest);
            App.State.Shaders.RecordProvenance(shader.SourcePackId, installedName, pick.Version.Source,
                projectId, pick.Version.Id, pick.Version.VersionNumber);

            await Task.Run(() =>
            {
                // Carry the tuned settings over to the new file.
                if (shader.SettingsPath is { Length: > 0 } sp && File.Exists(sp))
                {
                    try { File.Copy(sp, dest + ".txt", overwrite: true); } catch { /* best effort */ }
                }
                if (!replacingSelf) App.State.Shaders.Delete(shader);
            }, ct);

            // Re-point the slot only if this pack was the active one.
            if (wasLoaded) App.State.Shaders.SetActiveShader(shader.SourcePackId, installedName);

            Cache.Invalidate(ScanCache<CachedShaderPack>.ScopeKey(shader.SourcePackId));
            await LoadAsync(force: true);
            Okay($"Updated {shader.DisplayName} to {pick.Version.VersionNumber}"
               + (wasLoaded ? " and kept it as this instance's shader." : ".")
               + (shader.HasSettings ? " Your tuned settings came across." : ""));
        }
        catch (OperationCanceledException)
        {
            _state.Cancelled("Update cancelled - the pack you had is untouched.");
        }
        catch (Exception ex) { Fail("That update failed.", ex); }
    }

    // ── drag and drop ────────────────────────────────────────────────────────

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedShaders(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Dropping shader files here opens the Import flow with the file already chosen
    /// (<see cref="ImportRequest.PrePickedPath"/>).
    /// </summary>
    /// <remarks>One file at a time; the import card handles a single file.</remarks>
    private async void OnListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        try
        {
            var files = DroppedShaders(e);
            if (files.Count == 0) return;
            if (files.Count > 1)
                Okay($"Importing {Path.GetFileName(files[0])} - drop them one at a time to give each its "
                   + "own instances.");
            await RunImportAsync(files[0]);
        }
        catch (Exception ex) { Fail("Those files could not be imported.", ex); }
    }

    /// <summary>The zips and unpacked shader folders in a drag payload. Anything else is ignored.</summary>
    private static List<string> DroppedShaders(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new();
        return (e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>())
            .Where(f => Directory.Exists(f)
                        || (File.Exists(f) && f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // ── keyboard ─────────────────────────────────────────────────────────────

    private async void GlobalKeys(object sender, KeyEventArgs e)
    {
        // The store browser can be open in the side panel while this page still exists behind it.
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

        // Rows don't take keyboard focus, so these act on the row last clicked.
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
                    else if (Installed(row) is { } installed) await RemoveFromInstanceAsync(row, installed);
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

    /// <summary>Reloads, then shows the message. Written before the reload, it would be cleared.</summary>
    private async Task ReloadThenSayAsync(string message)
    {
        try { await LoadAsync(force: true); }
        finally { Okay(message); }
    }

    /// <summary>Shows a short error. PageState logs the exception instead of showing its text.</summary>
    private void Fail(string message, Exception? ex = null) => _state.Error(message, ex);
}

// ── view models ──────────────────────────────────────────────────────────────

/// <summary>One instance's shader packs as the scan cache remembers them.</summary>
public sealed record CachedShaderPack(
    string FileName,
    bool IsFolder,
    long ModifiedTicks,
    long SizeBytes,
    bool HasSettings,
    byte Origin);

/// <summary>One chip in the folder strip.</summary>
/// <remarks>No brushes: the selected look is a trigger on <see cref="IsActive"/>, so accent changes
/// repaint existing chips. Mutable because the strip is diffed rather than rebuilt.</remarks>
public sealed class ShaderFolderChipVm : System.ComponentModel.INotifyPropertyChanged
{
    private string _countLabel;
    private bool _isActive;

    /// <param name="id">The folder name, "" for All, or the page's reserved <c>:new</c> id.</param>
    /// <param name="label">What the chip says.</param>
    /// <param name="icon">An MDL2 glyph, or empty.</param>
    /// <param name="count">How many rows are in it; zero shows no count.</param>
    /// <param name="isActive">True for the chip the page is currently filtered by.</param>
    /// <param name="tip">The chip's tooltip.</param>
    public ShaderFolderChipVm(string id, string label, string icon, int count, bool isActive, string tip)
    {
        Id = id;
        Label = label;
        Icon = icon;
        ToolTipText = tip;
        _countLabel = count > 0 ? $"({count})" : "";
        _isActive = isActive;
    }

    /// <inheritdoc cref="ShaderFolderChipVm(string,string,string,int,bool,string)"/>
    public string Id { get; }
    public string Label { get; }
    public string Icon { get; }

    /// <summary>The chip's tooltip. Fixed; only the count changes.</summary>
    public string ToolTipText { get; }

    /// <summary>"(4)", or empty when there is nothing in it.</summary>
    public string CountLabel
    {
        get => _countLabel;
        private set { if (_countLabel != value) { _countLabel = value; Raise(nameof(CountLabel)); } }
    }

    /// <summary>The chip the page is filtered by. A bool, not a brush; see the class remarks.</summary>
    public bool IsActive
    {
        get => _isActive;
        private set { if (_isActive != value) { _isActive = value; Raise(nameof(IsActive)); } }
    }

    /// <summary>False for All and the New folder chip, which can't be renamed.</summary>
    public bool IsUserFolder => Id.Length > 0 && !Id.StartsWith(':');

    /// <summary>True for the trailing "New folder" chip, which makes a folder rather than
    /// selecting one. The shared chip style triggers on this to draw it as an outline.</summary>
    public bool IsNewAction => Id == ":new";

    /// <summary>Folds a freshly built chip into this one, for <see cref="ListDiff.Apply"/>.</summary>
    /// <remarks>Copy every property that can change; anything left out stops updating once the chip is
    /// on screen.</remarks>
    public void CopyFrom(ShaderFolderChipVm fresh)
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
public sealed record ShaderScopeItem(Guid? Id, string Label);
