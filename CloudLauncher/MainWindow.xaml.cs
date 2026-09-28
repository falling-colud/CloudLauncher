using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using CloudLauncher.Animations;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using CloudLauncher.Views;

namespace CloudLauncher;

public partial class MainWindow : Window, IDialogHost
{
    // ── master page state ────────────────────────────────────────────────────
    private enum MasterPage { None, Sharing, Packs, Worlds, Mods, ResourcePacks, Shaders, Servers, Configs, Storage }
    private MasterPage _currentMaster = MasterPage.None;

    /// <summary>Master pages kept alive between navigations, so reopening one refreshes it instead
    /// of rebuilding it. Only pages that implement <see cref="IReusablePage"/> go in here (see that
    /// interface for what they have to handle).</summary>
    /// <remarks>Bounded by the number of nav buttons, and none of them hosts a web view, so this is
    /// cheap. It is separate from the frame journal, which stays drained.</remarks>
    private readonly Dictionary<MasterPage, Page> _masterPages = new();

    // ── side-panel state ─────────────────────────────────────────────────────
    private enum SidePanelKind
    {
        None, Settings, Account, Teams, McAccount, McDefaults,
        PackDetail, ModExplorer, ModManagement, FileManagement, WorldDetail, PackBrowser, WorldBrowser, ModDetail,
        ResourcePackDetail, LocalResourcePackDetail, ShaderPackDetail, ResourcePackBrowser,
        ResourcePackExplorer, ShaderBrowser, BundleDetail
    }
    private SidePanelKind _currentSidePanel = SidePanelKind.None;
    private string _currentSidePanelTitle = "";
    private readonly Stack<(SidePanelKind kind, string title, Page page)> _sideStack = new();
    // Default side-panel fraction of the content area when first opened.
    private const double SidePanelDefaultFraction = 0.45;
    private const double SidePanelMinPx = 309;
    private const double SidePanelMaxFraction = 0.772;

    /// <summary>The master list's readable minimum, and the narrowest side panel worth showing
    /// beside it: below their sum the panel covers the master instead (see
    /// <see cref="LayoutSidePanel"/>).</summary>
    private const double MainMinPx = 360;
    private const double SidePanelUsablePx = 460;

    /// <summary>True while the panel covers the master only because the window is too narrow, so the
    /// side-by-side split is recomputed rather than restored when the window grows again.</summary>
    private bool _autoCover;
    private HwndSource? _source;

    // ── sidebar state ────────────────────────────────────────────────────────
    // Starts collapsed (icon-only). Toggle button expands to 240px.
    private bool _sidebarCollapsed = true;

    // Keep this in step with the sidebar in MainWindow.xaml and with NavLabelNames below.
    private static readonly string[] NavButtonNames =
        { "NavPacks", "NavWorlds", "NavMods", "NavResourcePacks", "NavShaders", "NavServers", "NavConfigs",
          "NavStorage", "NavSharing", "NavDiscord", "NavAccount", "NavSettings", "NavDev" };

    private static readonly string[] NavLabelNames =
        { "NavPacksLabel", "NavWorldsLabel", "NavModsLabel", "NavResourcePacksLabel", "NavShadersLabel",
          "NavServersLabel", "NavConfigsLabel", "NavStorageLabel",
          "NavSharingLabel", "NavDiscordLabel", "NavAccountLabel", "NavSettingsLabel", "NavDevLabel" };

    public MainWindow()
    {
        InitializeComponent();
        Loaded    += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        SizeChanged   += (_, _) => LayoutSidePanel();
        StateChanged  += (_, _) => UpdateMaximizeIcon();
        // A standalone Frame keeps a navigation journal whose entries keep their pages alive, so
        // every page ever shown (with its WebView2 hosts and collections) would stay in memory. We
        // keep our own back stack (_sideStack) and never call GoBack, so drain the journal after
        // each navigation.
        // Navigated also fires for nested frames (ModManagementView's BrowseFrame), so drain the
        // frame we subscribed to rather than the sender: a nested frame journals into its parent,
        // and RemoveBackEntry throws on a frame without its own journal.
        MainFrame.Navigated += (_, _) => DrainFrameJournal(MainFrame);
        SideFrame.Navigated += (_, _) => DrainFrameJournal(SideFrame);
    }

    /// <summary>Empties <paramref name="f"/>'s back-stack so discarded pages aren't rooted by it.</summary>
    private static void DrainFrameJournal(Frame f)
    {
        // A Frame creates its journal lazily, on the first navigation that needs a back entry.
        // Before that, RemoveBackEntry() throws "This operation is available only when Frame has
        // its own journal". CanGoBack is false in that case and when the stack is empty, so it
        // guards both.
        while (f.CanGoBack && f.RemoveBackEntry() != null) { }
    }

    // ── lifecycle ────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.SessionExpired += () => Dispatcher.Invoke(() =>
        {
            MessageBox.Show(this,
                "Your CloudLauncher session has expired. Sign in again for your account's instances, "
                + "sharing and sync, or continue without signing in.",
                "CloudLauncher", MessageBoxButton.OK, MessageBoxImage.Information);
            NavigateToLogin();
        });

        // Refresh the chip whenever the account list changes from anywhere.
        App.State.MinecraftAccounts.AccountsChanged += () => Dispatcher.Invoke(UpdateMcChip);

        // Apply the default collapsed state (XAML sets column to 60,
        // but we need the labels hidden and button alignment set too).
        ApplySidebarVisualState();

        UpdateChrome();
        UpdateMaximizeIcon();
        ApplyUiScale();
        UiScale.Changed += ApplyUiScale;

        // ConnectivityChanged is a static-lifetime event on AppState, so OnClosed has to
        // unsubscribe (see the matching -= there).
        App.State.ConnectivityChanged += OnConnectivityChanged;
        OfflineBar.RetryRequested += OnOfflineRetry;
        // Sync now rather than on the next change: a call may already have failed during startup.
        OfflineBar.Sync();

        // Signed in or not: an account is only needed for its online features (sharing, hosting,
        // syncing), and the Account button offers the sign-in page.
        NavigateToPacks();

        _ = CheckForLauncherUpdateAsync();
    }

    /// <summary>Applies the global launcher scale as a layout zoom on the whole window, and keeps the
    /// draggable caption region aligned with the scaled title bar. Safe to call repeatedly.</summary>
    public void ApplyUiScale()
    {
        var s = UiScale.Launcher;
        RootScale.ScaleX = RootScale.ScaleY = s;
        try
        {
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome is not null) chrome.CaptionHeight = 40 * s;
        }
        catch { /* frozen chrome: caption buttons still work via IsHitTestVisibleInChrome */ }
    }

    private bool _updatePrompted;

    /// <summary>Best-effort launch check: if the server has a newer build, prompt the user once.</summary>
    private async Task CheckForLauncherUpdateAsync()
    {
        if (_updatePrompted) return;
        var release = await App.State.Update.CheckForUpdateAsync();
        if (release is null || _updatePrompted) return;
        _updatePrompted = true;

        new UpdateDialog(release) { Owner = this }.ShowDialog();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WndProc);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        UiScale.Changed -= ApplyUiScale;
        App.State.ConnectivityChanged -= OnConnectivityChanged;
        OfflineBar.RetryRequested -= OnOfflineRetry;
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    // ── connectivity ─────────────────────────────────────────────────────────

    /// <summary>Raised on the UI thread by <see cref="AppState"/> whenever the server's
    /// reachability flips, in either direction.</summary>
    private void OnConnectivityChanged()
    {
        OfflineBar.Sync();
        // The three server-backed nav items explain themselves differently offline, and the tooltip
        // is the only place that message lives.
        UpdateChrome();
    }

    private async void OnOfflineRetry()
    {
        OfflineBar.SetBusy(true);
        try { await RetryCurrentPageAsync(); }
        finally
        {
            OfflineBar.SetBusy(false);
            OfflineBar.Sync();   // the retry may have succeeded, in which case the banner goes away
        }
    }

    /// <summary>
    /// Re-runs whatever the current master page loads. Pages opt into keeping their scroll position
    /// and selection by implementing <see cref="IRefreshablePage"/>; the rest are rebuilt.
    /// </summary>
    private async Task RetryCurrentPageAsync()
    {
        if (MainFrame.Content is IRefreshablePage page)
        {
            // A failed retry is normal here (the network was down a moment ago), so it must not
            // reach the app-level crash handler.
            try { await page.RefreshAsync(); }
            catch (Exception ex) { AppLog.LogError("net", ex); }
            return;
        }

        RebuildCurrentMaster();
    }

    /// <summary>Navigates a fresh copy of the current master page, which re-runs its own load.</summary>
    /// <remarks>Drops the cached instance first, or <see cref="ShowMaster"/> would hand the same
    /// page back.</remarks>
    private void RebuildCurrentMaster()
    {
        _masterPages.Remove(_currentMaster);
        switch (_currentMaster)
        {
            case MasterPage.Sharing:       MainFrame.Navigate(new SharingHubView(this));   break;
            case MasterPage.Packs:         MainFrame.Navigate(new PackListView(this));     break;
            case MasterPage.Worlds:        MainFrame.Navigate(new WorldsView(this));       break;
            case MasterPage.Mods:          MainFrame.Navigate(new ModsView(this));         break;
            case MasterPage.ResourcePacks: MainFrame.Navigate(new ResourcePacksView(this)); break;
            case MasterPage.Shaders:       MainFrame.Navigate(new ShaderPacksView(this));  break;
            case MasterPage.Servers:       MainFrame.Navigate(new ServersView(this));      break;
            case MasterPage.Configs:       MainFrame.Navigate(new ConfigHubView(this));    break;
            case MasterPage.Storage:       MainFrame.Navigate(new SharingStoragePanel(this)); break;
            // MasterPage.None is the login screen: there is nothing cached there to ask for again.
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (WindowWorkArea.TryHandleGetMinMaxInfo(hwnd, msg, lParam))
            handled = true;

        return IntPtr.Zero;
    }

    // ── master navigation ────────────────────────────────────────────────────

    /// <summary>Shows one of the master pages, reusing the live instance when there is one.</summary>
    /// <param name="build">Constructs the page. Called only on a cache miss.</param>
    /// <remarks>Pages that don't implement <see cref="IReusablePage"/> are built fresh every
    /// time.</remarks>
    private void ShowMaster(MasterPage which, Func<Page> build)
    {
        Sidebar.IsEnabled = true;
        ResetSidePanel();

        if (!_masterPages.TryGetValue(which, out var page))
        {
            page = build();
            // Only cache pages that opted in.
            if (page is IReusablePage) _masterPages[which] = page;
        }

        // Navigating a page that is already the content would re-run the transition for nothing.
        if (!ReferenceEquals(MainFrame.Content, page))
            MainFrame.Navigate(page);

        _currentMaster = which;
        UpdateChrome();
    }

    /// <summary>Drops the cached copy of one master page, and rebuilds it if it is on screen.</summary>
    /// <remarks>For when a page's contents are invalidated by something it can't see, like the
    /// instances folder moving under <c>PackListView</c>. Re-navigating would show the same stale
    /// objects.</remarks>
    private void EvictMaster(MasterPage which)
    {
        _masterPages.Remove(which);
        if (_currentMaster == which) RebuildCurrentMaster();
    }

    /// <summary>Drops every cached master page. Used when the signed-in account changes, so one user's
    /// rows can never be left on screen for the next.</summary>
    private void EvictAllMasters() => _masterPages.Clear();

    /// <summary>The instances folder has moved: throw away every page built from paths inside the
    /// old one.</summary>
    /// <remarks>Not just Instances: Worlds, Resource packs, Shader packs, Servers and Config &amp;
    /// scripts all hold rows keyed by paths under the packs root (save folders, pack zips,
    /// <c>servers.dat</c>, config files). Since those pages are kept alive, they would otherwise
    /// keep showing the old folder.</remarks>
    public void OnInstancesRootMoved()
    {
        App.State.LocalPacks.Invalidate();
        EvictAllMasters();
        RebuildCurrentMaster();
    }

    /// <summary>The sign-in page, in place of the current page.</summary>
    /// <remarks>The sidebar stays usable: signing in is optional, so the rest of the launcher is one
    /// click away from here.</remarks>
    public void NavigateToLogin()
    {
        ResetSidePanel();
        Sidebar.IsEnabled = true;
        // Signing in or out must not leave the previous account's instances, worlds or teams cached
        // where the next page would navigate straight back into them.
        EvictAllMasters();
        MainFrame.Navigate(new LoginView(this));
        _currentMaster = MasterPage.None;
        UpdateChrome();
    }

    /// <summary>After a sign-in: every page is rebuilt for the account, starting at Instances.</summary>
    public void OnSignedIn()
    {
        EvictAllMasters();
        _accountReadFor = null;
        NavigateToPacks();
    }

    /// <summary>After a sign-out (or a deleted account): back to Instances, which now lists what is on
    /// this PC only.</summary>
    public void OnSignedOut()
    {
        ResetSidePanel();
        EvictAllMasters();
        _accountReadFor = null;
        NavigateToPacks();
    }

    public void NavigateToPacks()
    {
        ShowMaster(MasterPage.Packs, () => new PackListView(this));
        // Pull the admin-managed global default rules so new packs get the same routing as on every
        // other launcher. Fire and forget; failures fall back to the local cache. The route needs an
        // account, and signed out the cached copy is what applies.
        if (App.State.Api.IsSignedIn) _ = App.State.Rules.SyncGlobalDefaultsFromServerAsync();
    }

    /// <summary>Rebuilds the Instances screen from scratch, discarding whatever is on it.</summary>
    /// <remarks><see cref="NavigateToPacks"/> reuses a <see cref="PackListView"/> that is already
    /// showing, to keep scroll position and folder selection. After the instances folder moves,
    /// every card was built from old paths, so this forces a fresh page. Does nothing unless
    /// Instances is on screen, and leaves the side panel alone so the Settings page that asked for
    /// this stays open.</remarks>
    public void RefreshPacks()
    {
        if (MainFrame.Content is not PackListView) return;
        // Evict rather than re-navigate: ShowMaster would otherwise hand back the very page whose
        // cards were all built from paths under the old root.
        EvictMaster(MasterPage.Packs);
    }

    public async Task RefreshPackTeamFoldersAsync()
    {
        if (MainFrame.Content is PackListView packs)
            await packs.RefreshTeamFoldersAsync();
    }

    public void AddOrUpdatePackList(PackSummary pack)
    {
        if (MainFrame.Content is PackListView packs)
            packs.AddOrUpdatePack(pack);
    }

    /// <summary>Refresh a pack card's cover after its image changed in the detail view.</summary>
    public void RefreshPackCover(Guid packId)
    {
        if (MainFrame.Content is PackListView packs)
            packs.RefreshPackCover(packId);
    }

    /// <summary>Refresh an open pack detail's hero after its image changed in the pack list.</summary>
    public void RefreshPackDetailHero(Guid packId)
    {
        if (SideFrame.Content is PackDetailView detail && detail.PackId == packId)
            detail.RefreshHeroIcon();
    }

    public void NavigateToWorlds() => ShowMaster(MasterPage.Worlds, () => new WorldsView(this));

    public void NavigateToMods() => ShowMaster(MasterPage.Mods, () => new ModsView(this));

    public void NavigateToResourcePacks() =>
        ShowMaster(MasterPage.ResourcePacks, () => new ResourcePacksView(this));

    public void NavigateToShaders() => ShowMaster(MasterPage.Shaders, () => new ShaderPacksView(this));

    public void NavigateToServers() => ShowMaster(MasterPage.Servers, () => new ServersView(this));

    public void NavigateToConfigs() => ShowMaster(MasterPage.Configs, () => new ConfigHubView(this));

    /// <summary>How much of this PC the launcher is using, and what it is.</summary>
    /// <remarks>Cached like the other content pages. It matters most here, since a full pass reads
    /// every file under every instance.</remarks>
    public void NavigateToStorage() =>
        ShowMaster(MasterPage.Storage, () => new SharingStoragePanel(this));

    public void NavigateToSharing()
    {
        ShowMaster(MasterPage.Sharing, () => new SharingHubView(this));
    }

    // ── side-panel openers ───────────────────────────────────────────────────

    /// <summary>The Teams side panel. No nav button opens it (Sharing has its own "People &amp;
    /// teams" tab), but the activity feed links team rows here.</summary>
    public void OpenTeams()     => OpenSidePanelFresh(SidePanelKind.Teams,     "Teams",              new TeamsView(this));
    public void OpenAccount()   => OpenSidePanelFresh(SidePanelKind.Account,   "Account",            new AccountPanel(this));
    public void OpenSettings()  => OpenSidePanelFresh(SidePanelKind.Settings,  "Settings",           new SettingsPanel(this));
    public void OpenMcAccount()  => OpenSidePanelFresh(SidePanelKind.McAccount, "Minecraft account",  new MinecraftAccountPanel(this));
    public void OpenMcDefaults() => OpenSidePanelPushed(SidePanelKind.McDefaults, "Minecraft defaults", new MCDefaultsPanel(this));

    public void OpenPackDetail(Guid packId, string packName) =>
        OpenSidePanelFresh(SidePanelKind.PackDetail, packName, new PackDetailView(this, packId));

    public void OpenWorldDetail(string worldId, string title) =>
        OpenSidePanelPushed(SidePanelKind.WorldDetail, title, new WorldDetailView(this, worldId));

    public void OpenModDetail(Guid modId, string title) =>
        OpenSidePanelPushed(SidePanelKind.ModDetail, title, new ModDetailView(this, modId));

    public void OpenModExplorerForPack(CloudLauncher.Shared.PackDetail pack, CloudLauncher.Services.ModSummary? showMod = null)
    {
        var page = new ModExplorerPage(this, pack);
        OpenSidePanelPushed(SidePanelKind.ModExplorer, $"Browse mods · {pack.Name}", page);
        if (showMod is not null) _ = page.ShowModAsync(showMod);
    }

    public void OpenModManagementForPack(CloudLauncher.Shared.PackDetail pack) =>
        OpenSidePanelPushed(SidePanelKind.ModManagement, $"Mods · {pack.Name}", new ModManagementView(this, pack));

    public void OpenFileManagementForPack(CloudLauncher.Shared.PackDetail pack) =>
        OpenSidePanelPushed(SidePanelKind.FileManagement, $"Files · {pack.Name}", new FileManagementView(this, pack));

    public void OpenPackBrowser() =>
        OpenSidePanelFresh(SidePanelKind.PackBrowser, "Download a pack", new PackBrowserView(this));

    public void OpenWorldBrowser() =>
        OpenSidePanelFresh(SidePanelKind.WorldBrowser, "Download a world", new WorldBrowserView(this));

    /// <summary>A world somebody hosts, by its id. Hosted worlds have no page of their own: this is
    /// Download a world, opened on that one.</summary>
    /// <remarks>Pushed, not fresh: it is opened from a row on another page (the Sharing
    /// overview), and Back has to return there.</remarks>
    public void OpenHostedWorld(Guid worldId, string title) =>
        OpenSidePanelPushed(SidePanelKind.WorldBrowser, title, new WorldBrowserView(this, worldId));

    public void OpenResourcePackDetail(Guid packId, string title) =>
        OpenSidePanelPushed(SidePanelKind.ResourcePackDetail, title,
            new ResourcePackDetailView(this, packId));

    /// <summary>One instance's copy of a resource pack, by that copy's
    /// <c>&lt;instanceGuid&gt;:&lt;fileName&gt;</c> key.</summary>
    /// <remarks>The instance's own Resources tab opens the page this way, so the key route stays.
    /// It is not the only way in: see <see cref="OpenLibraryResourcePackDetail"/>.</remarks>
    public void OpenLocalResourcePackDetail(string key, string title) =>
        OpenSidePanelPushed(SidePanelKind.LocalResourcePackDetail, title,
            new LocalResourcePackDetailView(this, key));

    /// <summary>The same page, for a pack the launcher holds that no instance has a copy of
    /// yet.</summary>
    /// <remarks>Content is only placed into instances on their next launch, so right after an
    /// import there may be no instance copy for the key above to name. The page is about the item;
    /// an instance's copy is just one way to name it.</remarks>
    public void OpenLibraryResourcePackDetail(LibraryItem item, string title) =>
        OpenSidePanelPushed(SidePanelKind.LocalResourcePackDetail, title,
            new LocalResourcePackDetailView(this, item));

    /// <summary>The shader pack's own page, the Shaders list's counterpart to
    /// <see cref="OpenLocalResourcePackDetail"/>.</summary>
    /// <remarks>Pushed rather than fresh, like its two siblings, so Back returns to the list it
    /// came from.</remarks>
    public void OpenShaderPackDetail(string key, string title) =>
        OpenSidePanelPushed(SidePanelKind.ShaderPackDetail, title,
            new ShaderPackDetailView(this, key));

    /// <inheritdoc cref="OpenLibraryResourcePackDetail"/>
    public void OpenLibraryShaderPackDetail(LibraryItem item, string title) =>
        OpenSidePanelPushed(SidePanelKind.ShaderPackDetail, title,
            new ShaderPackDetailView(this, item));

    public void OpenResourcePackBrowser() =>
        OpenSidePanelFresh(SidePanelKind.ResourcePackBrowser, "Download resource packs",
            new ResourcePackBrowserView(this));

    /// <summary>A hosted bundle's own page (a shader pack, config set, KubeJS scripts or data pack
    /// someone hosts), in the side panel like every other item page.</summary>
    /// <remarks>The page is a UserControl, so it travels in a bare Page, and closing it pops the
    /// panel.</remarks>
    public void OpenBundleDetail(Guid bundleId, string title)
    {
        var bundles = new Services.ContentBundleService(App.State.Api, App.State.Settings, App.State.Packs);
        var view = Views.BundleDetailView.ForExisting(this, bundles, bundleId, title, publishOnOpen: false);
        // The page has no padding of its own; this is the inset the other side-panel pages use.
        view.Margin = new Thickness(24, 8, 24, 12);
        var page = new Page { Content = view };
        view.Closed += _ => CloseSidePanel();
        OpenSidePanelPushed(SidePanelKind.BundleDetail, title, page);
    }

    /// <summary>Opens the shader store as its own side panel, the way every other Browse works.
    /// Returns the page so the Shaders list can hear about an install and re-scan.</summary>
    public ShaderBrowserView OpenShaderBrowser(
        IReadOnlyList<CloudLauncher.Shared.PackSummary> packs, Guid? preferredTarget)
    {
        var page = new ShaderBrowserView(this, packs, preferredTarget);
        OpenSidePanelFresh(SidePanelKind.ShaderBrowser, "Download shader packs", page);
        return page;
    }

    public void OpenResourcePackExplorerForPack(CloudLauncher.Shared.PackDetail pack) =>
        OpenSidePanelPushed(SidePanelKind.ResourcePackExplorer,
            $"Browse resource packs · {pack.Name}",
            new ResourcePackExplorerPage(this, pack));

    public void OpenMinecraftHost(CloudLauncher.Shared.PackDetail pack, System.Diagnostics.Process process)
    {
        // With the custom game window off, Minecraft runs in its own window. The launcher already
        // started and tracks the process (play time, instance state), so there is nothing to host.
        if (!App.State.Settings.UseCustomGameWindow)
            return;

        new MinecraftHostWindow(this, pack, process).Show();
    }

    // ── side-panel mechanics ─────────────────────────────────────────────────

    private void OpenSidePanelFresh(SidePanelKind which, string title, Page content)
    {
        _sideStack.Clear();
        SetSidePanelContent(which, title, content);
    }

    private void OpenSidePanelPushed(SidePanelKind which, string title, Page content)
    {
        if (_currentSidePanel != SidePanelKind.None && SideFrame.Content is Page current)
            _sideStack.Push((_currentSidePanel, _currentSidePanelTitle, current));
        SetSidePanelContent(which, title, content);
    }

    private void SetSidePanelContent(SidePanelKind which, string title, Page content)
    {
        // Play the slide-in on the host only if the panel was closed before this call.
        bool wasClosed = SidePanelHost.Visibility != Visibility.Visible;

        _currentSidePanel = which;
        _currentSidePanelTitle = title;
        SidePanelHost.Visibility = Visibility.Visible;
        LayoutSidePanel();
        SidePanelHost.UpdateLayout();
        SideFrame.Navigate(content);
        RefreshNavCheckedStates();

        if (wasClosed)
            Animate.SlideFadeIn(SidePanelHost, fromX: 28, fromY: 0, ms: 260);
    }

    /// <summary>Back arrow: pop one level, or close the panel if nothing left.</summary>
    public void CloseSidePanel()
    {
        if (_sideStack.Count > 0)
        {
            var prev = _sideStack.Pop();
            _currentSidePanel = prev.kind;
            _currentSidePanelTitle = prev.title;
            SideFrame.Navigate(prev.page);
            LayoutSidePanel();
            RefreshNavCheckedStates();
            return;
        }
        _currentSidePanel = SidePanelKind.None;
        _currentSidePanelTitle = "";
        SidePanelHost.Visibility = Visibility.Collapsed;
        SideFrame.Content = null;
        LayoutSidePanel();
        RefreshNavCheckedStates();
    }

    /// <summary>
    /// Close the instance's detail page if it is open in the side panel. Called when an
    /// instance is deleted so its now-stale page doesn't linger. Handles both the visible
    /// panel and any copies sitting in the back stack.
    /// </summary>
    public void CloseSidePanelForPack(Guid packId)
    {
        static bool IsDeadPackDetail(SidePanelKind kind, Page? page, Guid packId) =>
            kind == SidePanelKind.PackDetail && page is PackDetailView pd && pd.PackId == packId;

        // Prune stale entries from the back stack so a later "back" can't land on a dead page.
        if (_sideStack.Count > 0 &&
            _sideStack.Any(e => IsDeadPackDetail(e.kind, e.page, packId)))
        {
            var kept = _sideStack.Where(e => !IsDeadPackDetail(e.kind, e.page, packId)).ToArray();
            _sideStack.Clear();
            for (int i = kept.Length - 1; i >= 0; i--)
                _sideStack.Push(kept[i]);
        }

        // If the visible panel is the deleted instance's page, pop back / close it.
        if (IsDeadPackDetail(_currentSidePanel, SideFrame.Content as Page, packId))
            CloseSidePanel();
    }

    /// <summary>Public re-layout hook so Settings can apply the side-panel toggle immediately.</summary>
    public void RelayoutSidePanel() => LayoutSidePanel();

    /// <summary>X button: clear stack and close entirely.</summary>
    public void ResetSidePanel()
    {
        _sideStack.Clear();
        _currentSidePanel = SidePanelKind.None;
        _currentSidePanelTitle = "";
        SidePanelHost.Visibility = Visibility.Collapsed;
        SideFrame.Content = null;
        LayoutSidePanel();
        RefreshNavCheckedStates();
    }

    /// <summary>
    /// Layout. By default side-by-side (master on left, panel on right) with a draggable splitter.
    /// If the user disabled side panels in Settings, the side panel covers the main content area
    /// (master is hidden while a secondary page is open) and a back arrow is shown.
    /// </summary>
    private void LayoutSidePanel()
    {
        if (_currentSidePanel == SidePanelKind.None)
        {
            MainColumn.MinWidth     = 360;         // restore readable master minimum (cover mode clears it)
            MainColumn.Width        = new GridLength(1, GridUnitType.Star);
            SplitterColumn.Width    = new GridLength(0);
            SidePanelColumn.Width   = new GridLength(0);
            SidePanelColumn.MinWidth = 0;          // clear minwidth so column truly collapses
            MainFrame.Visibility = Visibility.Visible;
            PanelSplitter.Visibility    = Visibility.Collapsed;
            SidePanelBackButton.Visibility = Visibility.Collapsed;
            return;
        }

        // Side panel is open: restore the minimum so it can't be dragged below 280 px.
        SidePanelColumn.MinWidth = SidePanelMinPx;

        // The mod explorer is a full browsing view (list + detail), so it is always shown
        // full-width, even with side-by-side panels on. File management too: a tree beside a file
        // pane doesn't fit in a 45% column.
        var forceCover = _currentSidePanel is SidePanelKind.ModExplorer or SidePanelKind.ModManagement
                                           or SidePanelKind.FileManagement;

        // ActualWidth is in window units; the grid (and its columns) live inside RootScale, so
        // convert to the scaled grid's coordinates before mixing with column widths.
        var contentPx = ActualWidth / Math.Max(RootScale.ScaleX, 0.1) - SidebarColumn.ActualWidth;
        if (contentPx < 100) contentPx = 1000;

        // Too narrow for both: below the width where a readable master and a usable page both fit,
        // the page covers the master (as with the "pages cover the main content" setting).
        // Side-by-side comes back when the window grows.
        var tooNarrow = contentPx < MainMinPx + 5 + SidePanelUsablePx;
        var useSideBySide = App.State.Settings.UseSidePanel && !forceCover && !tooNarrow;

        if (!useSideBySide)
        {
            _autoCover = tooNarrow && App.State.Settings.UseSidePanel && !forceCover;
            // Cover mode: the side panel takes the whole content area and the master is hidden.
            // Clear the master column's MinWidth too, since MinWidth overrides Width and would
            // leave an empty band on the left.
            MainColumn.MinWidth   = 0;
            MainColumn.Width      = new GridLength(0);
            SplitterColumn.Width  = new GridLength(0);
            SidePanelColumn.Width = new GridLength(1, GridUnitType.Star);
            MainFrame.Visibility = Visibility.Collapsed;
            PanelSplitter.Visibility = Visibility.Collapsed;
            // Back arrow always visible in cover mode so the user can return to master.
            SidePanelBackButton.Visibility = Visibility.Visible;
            return;
        }

        // Side-by-side mode (default): restore the master column's readable minimum.
        MainColumn.MinWidth = MainMinPx;
        MainFrame.Visibility = Visibility.Visible;

        // Coming back from an automatic cover: the columns still hold cover's 0 / 1* split, so the
        // initial split is computed again below.
        if (_autoCover)
        {
            _autoCover = false;
            SidePanelColumn.Width = new GridLength(0);
        }

        // Only set the initial split when the side panel is transitioning from closed.
        // After the user drags the splitter we leave their ratio intact.
        if (SidePanelColumn.Width.Value == 0 || SidePanelColumn.Width.GridUnitType == GridUnitType.Pixel)
        {
            // Never opens narrower than a usable page (the user can still drag it down to
            // SidePanelMinPx); 45% of a small window wraps a word per line.
            var panelPx = Math.Max(
                Math.Min(contentPx * SidePanelDefaultFraction, contentPx * SidePanelMaxFraction),
                SidePanelUsablePx);
            var mainPx = contentPx - panelPx - 5;

            MainColumn.Width      = new GridLength(Math.Max(mainPx, 1), GridUnitType.Star);
            SidePanelColumn.Width = new GridLength(panelPx,             GridUnitType.Star);
        }

        SplitterColumn.Width = new GridLength(5);
        PanelSplitter.Visibility = Visibility.Visible;

        SidePanelBackButton.Visibility =
            _sideStack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── in-window modal dialogs ──────────────────────────────────────────────

    public Task<bool> ShowConfirmAsync(string title, string message,
        string confirmText = "Yes", string cancelText = "Cancel", bool danger = false)
    {
        var overlay = new Views.DialogOverlay();
        overlay.Configure(title, message, confirmText, cancelText, danger);
        return ShowOverlayAsync(overlay);
    }

    public Task ShowMessageAsync(string title, string message, string okText = "OK")
    {
        var overlay = new Views.DialogOverlay();
        overlay.Configure(title, message, okText, null, false);
        return ShowOverlayAsync(overlay);
    }

    private async Task<bool> ShowOverlayAsync(Views.DialogOverlay overlay)
    {
        DialogLayer.Children.Add(overlay);
        SetOverlayVisible(true);
        DialogStackChanged?.Invoke();
        try { return await overlay.Result; }
        finally
        {
            DialogLayer.Children.Remove(overlay);
            if (DialogLayer.Children.Count == 0) SetOverlayVisible(false);
            DialogStackChanged?.Invoke();
        }
    }

    /// <summary>True while a card or dialog is up in the dialog layer.</summary>
    public static bool IsOverlayVisible { get; private set; }

    /// <summary>Raised when the dialog layer appears or disappears. Native (airspace) surfaces like
    /// the WebView2 description views paint over anything WPF draws, so they swap to a snapshot of
    /// themselves while an overlay is up.</summary>
    public static event Action<bool>? OverlayChanged;

    /// <summary>Raised after every card or dialog added to or removed from the dialog layer, including
    /// one stacked over another. See <see cref="IsUnderCard"/>.</summary>
    public static event Action? DialogStackChanged;

    /// <summary>Whether a card or dialog in the dialog layer sits over <paramref name="element"/>, so a
    /// native surface there has to hide.</summary>
    /// <remarks>
    /// Something on a card is only under the cards stacked after its own: the changelog on the update
    /// review or the version picker has to show on its own card, and hide once a changelog card opens
    /// over that one. Anything else in a launcher window is under any card; other windows have no
    /// dialog layer.
    /// </remarks>
    public static bool IsUnderCard(DependencyObject element)
    {
        if (Window.GetWindow(element) is not MainWindow main) return false;
        var layer = main.DialogLayer;
        if (layer.Children.Count == 0) return false;
        for (var d = element; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is UIElement card && ReferenceEquals(System.Windows.Media.VisualTreeHelper.GetParent(d), layer))
                return layer.Children.IndexOf(card) < layer.Children.Count - 1;
        }
        return true;
    }

    private void SetOverlayVisible(bool visible)
    {
        DialogLayer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (IsOverlayVisible == visible) return;
        IsOverlayVisible = visible;
        OverlayChanged?.Invoke(visible);
    }

    private static readonly System.Windows.Media.Brush OverlayBackdrop = MakeBackdrop();
    private static System.Windows.Media.Brush MakeBackdrop()
    {
        var b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xA6, 0, 0, 0));
        b.Freeze();
        return b;
    }

    /// <summary>Hosts an arbitrary card element centred over a dimming backdrop in the dialog layer
    /// and awaits <paramref name="completion"/>. <paramref name="onBackdropCancel"/> (if given) runs
    /// when the backdrop is clicked.</summary>
    public async Task ShowCardAsync(
        FrameworkElement card, Task completion, Action? onBackdropCancel = null,
        ResizableCardSpec? resizable = null)
    {
        var layer = new Grid();
        var backdrop = new Border { Background = OverlayBackdrop };
        if (onBackdropCancel is not null)
            backdrop.MouseLeftButtonDown += (_, _) => onBackdropCancel();
        layer.Children.Add(backdrop);

        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        // A resizable card is shown inside its own container, which owns the grip and the remembered
        // size; everything after this point treats the two cases the same.
        layer.Children.Add(resizable is null ? card : ResizableCard.Wrap(card, resizable));

        DialogLayer.Children.Add(layer);
        SetOverlayVisible(true);
        DialogStackChanged?.Invoke();
        try { await completion; }
        finally
        {
            DialogLayer.Children.Remove(layer);
            if (DialogLayer.Children.Count == 0) SetOverlayVisible(false);
            DialogStackChanged?.Invoke();
        }
    }

    /// <summary>In-window text prompt. Returns the entered text, or null if cancelled.</summary>
    public async Task<string?> PromptAsync(string title, string label, string initial = "")
    {
        var card = new InputDialogCard(title, label, initial);
        await ShowCardAsync(card, card.Result, card.Cancel);
        return card.Result.Result;
    }

    private void OnFullCloseSidePanel(object sender, RoutedEventArgs e) => ResetSidePanel();
    private void OnBackSidePanel(object sender, RoutedEventArgs e)
    {
        // Give the open page first chance at Back (e.g. the mod page returns to its previous tab).
        if (SideFrame.Content is ISidePanelBackHandler handler && handler.TryHandleBack()) return;
        CloseSidePanel();
    }

    // ── sidebar collapse ─────────────────────────────────────────────────────

    private void OnSidebarToggle(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        AnimateSidebarWidth(_sidebarCollapsed ? 60 : 240);
        AnimateSidebarLabels(show: !_sidebarCollapsed);
    }

    /// <summary>Smoothly ease the sidebar column between its collapsed/expanded widths.</summary>
    private void AnimateSidebarWidth(double toPx)
    {
        var target = new GridLength(toPx);
        var anim = new GridLengthAnimation
        {
            From = SidebarColumn.Width,
            To = target,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        // Clear the held animation and pin the final value so later direct sets still work.
        anim.Completed += (_, _) =>
        {
            SidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, null);
            SidebarColumn.Width = target;
        };
        SidebarColumn.BeginAnimation(ColumnDefinition.WidthProperty, anim);
    }

    /// <summary>Fade the nav labels in (after space opens) or out (before the column shrinks).</summary>
    private void AnimateSidebarLabels(bool show)
    {
        var labels = new List<TextBlock>();
        if (FindName("SidebarHeaderLabel") is TextBlock header) labels.Add(header);
        foreach (var name in NavLabelNames)
            if (FindName(name) is TextBlock lbl) labels.Add(lbl);

        foreach (var lbl in labels)
        {
            if (show)
            {
                lbl.Visibility = Visibility.Visible;
                lbl.Opacity = 0;
                var a = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
                {
                    BeginTime = TimeSpan.FromMilliseconds(90),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                a.Completed += (_, _) => { lbl.BeginAnimation(UIElement.OpacityProperty, null); lbl.Opacity = 1; };
                lbl.BeginAnimation(UIElement.OpacityProperty, a);
            }
            else
            {
                var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110));
                a.Completed += (_, _) =>
                {
                    lbl.BeginAnimation(UIElement.OpacityProperty, null);
                    lbl.Opacity = 1;                       // reset; collapsed visibility hides it
                    lbl.Visibility = Visibility.Collapsed;
                };
                lbl.BeginAnimation(UIElement.OpacityProperty, a);
            }
        }
    }

    /// <summary>Applies the current _sidebarCollapsed flag. Every nav row has a fixed 60 px icon
    /// column and a flexible label column, so only the labels need showing or hiding.</summary>
    private void ApplySidebarVisualState()
    {
        SidebarHeaderLabel.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var name in NavLabelNames)
            if (FindName(name) is TextBlock lbl)
                lbl.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── window chrome ────────────────────────────────────────────────────────

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeIcon()
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized
            ? char.ConvertFromUtf32(0xE923)
            : char.ConvertFromUtf32(0xE922);
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        RootBorder.Padding     = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    // ── sidebar nav clicks ───────────────────────────────────────────────────

    private void OnNavSharing(object sender, RoutedEventArgs e) => NavigateToSharing();

    private void OnNavDiscord(object sender, RoutedEventArgs e)
    {
        // A link rather than a page, so it must not stay lit the way the page buttons do.
        NavDiscord.IsChecked = false;
        SafeLaunch.OpenUrl(CloudLauncher.Shared.Legal.DiscordUrl);
    }
    private void OnNavPacks(object sender, RoutedEventArgs e)  => NavigateToPacks();
    private void OnNavWorlds(object sender, RoutedEventArgs e) => NavigateToWorlds();
    private void OnNavMods(object sender, RoutedEventArgs e)           => NavigateToMods();
    private void OnNavResourcePacks(object sender, RoutedEventArgs e) => NavigateToResourcePacks();
    private void OnNavShaders(object sender, RoutedEventArgs e) => NavigateToShaders();
    private void OnNavServers(object sender, RoutedEventArgs e) => NavigateToServers();
    private void OnNavConfigs(object sender, RoutedEventArgs e) => NavigateToConfigs();
    private void OnNavStorage(object sender, RoutedEventArgs e) => NavigateToStorage();

    /// <summary>The account panel while signed in, the sign-in page otherwise.</summary>
    private void OnNavAccount(object sender, RoutedEventArgs e)
    {
        if (App.State.Settings.IsLoggedIn) ToggleTopLevel(SidePanelKind.Account, OpenAccount);
        else if (_currentMaster == MasterPage.None) NavigateToPacks();   // pressed again on the sign-in page
        else NavigateToLogin();
    }
    private void OnNavSettings(object sender, RoutedEventArgs e) => ToggleTopLevel(SidePanelKind.Settings, OpenSettings);

    private void OnNavDev(object sender, RoutedEventArgs e)
    {
        if (!App.State.Settings.IsAdmin) { UpdateChrome(); return; }
        new DevWindow { Owner = this }.ShowDialog();
        // A toggle button latches on when clicked, and nothing else unchecks this one.
        NavDev.IsChecked = false;
    }
    /// <summary>
    /// Title-bar chip: dropdown with all saved accounts and quick actions to switch / add / manage.
    /// </summary>
    private void OnMcAccountChip(object sender, RoutedEventArgs e)
    {
        var svc = App.State.MinecraftAccounts;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = McAccountChip };

        if (svc.Accounts.Count == 0)
        {
            var none = new System.Windows.Controls.MenuItem { Header = "(no saved accounts yet)", IsEnabled = false };
            menu.Items.Add(none);
        }
        else
        {
            foreach (var acc in svc.Accounts)
            {
                var isCurrent = svc.Current?.Id == acc.Id;
                var item = new System.Windows.Controls.MenuItem
                {
                    Header     = (isCurrent ? "✓  " : "    ") + acc.Username
                              + (acc.Kind == Services.MinecraftAccountKind.Offline ? "  ·  offline" : "  ·  Microsoft"),
                    IsEnabled  = !isCurrent
                };
                var id = acc.Id;
                item.Click += (_, _) => App.State.MinecraftAccounts.Switch(id);
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new System.Windows.Controls.Separator());
        var add = new System.Windows.Controls.MenuItem { Header = "Add or manage accounts..." };
        add.Click += (_, _) => OpenMcAccount();
        menu.Items.Add(add);

        menu.IsOpen = true;
    }

    /// <summary>Clicking an already-active top-level panel closes it; otherwise opens it.</summary>
    private void ToggleTopLevel(SidePanelKind kind, Action open)
    {
        if (_currentSidePanel == kind && _sideStack.Count == 0)
            ResetSidePanel();
        else
            open();
    }

    // ── nav check-state sync ─────────────────────────────────────────────────

    private void RefreshNavCheckedStates()
    {
        // A ToggleButton checks itself on click, so an entry missing here would stay lit after you
        // navigate away.
        NavSharing.IsChecked  = _currentMaster == MasterPage.Sharing;
        NavPacks.IsChecked    = _currentMaster == MasterPage.Packs;
        NavWorlds.IsChecked   = _currentMaster == MasterPage.Worlds;
        NavMods.IsChecked           = _currentMaster == MasterPage.Mods;
        NavResourcePacks.IsChecked = _currentMaster == MasterPage.ResourcePacks;
        NavShaders.IsChecked       = _currentMaster == MasterPage.Shaders;
        NavServers.IsChecked       = _currentMaster == MasterPage.Servers;
        NavConfigs.IsChecked       = _currentMaster == MasterPage.Configs;
        NavStorage.IsChecked       = _currentMaster == MasterPage.Storage;
        NavAccount.IsChecked  = _currentSidePanel == SidePanelKind.Account
                             || _currentSidePanel == SidePanelKind.McAccount
                             || _currentMaster == MasterPage.None;   // the sign-in page
        NavSettings.IsChecked = _currentSidePanel == SidePanelKind.Settings;
    }

    // ── chrome / user state ──────────────────────────────────────────────────

    /// <summary>Gates each nav item on what it needs, with a tooltip saying so.</summary>
    /// <remarks>Pages that read this PC's disk are always available. Sharing is the server's page
    /// and needs a signed-in account; it stays enabled offline and explains itself on the page. A
    /// signed-in user stays signed in while offline, so <c>IsLoggedIn</c> is not an online
    /// check.</remarks>
    public void UpdateChrome()
    {
        var loggedIn = App.State.Settings.IsLoggedIn;
        var offline  = App.State.IsOffline;
        var username = App.State.Settings.Username ?? "";

        NavAccountLabel.Text = loggedIn ? username : "Sign in";

        // Local disk only; none of these need the network.
        GateNav(NavPacks,         true, "Instances");
        GateNav(NavWorlds,        true, "Worlds");
        GateNav(NavResourcePacks, true, "Resource packs");
        GateNav(NavShaders,       true, "Shader packs");
        GateNav(NavServers,       true, "Servers");
        GateNav(NavConfigs,       true, "Config & scripts");
        // Mods manages the mods already on disk across every instance, which needs no account. Its
        // hosted-store chips do, and the page says so itself.
        GateNav(NavMods,          true, "Mods");
        // Every figure on it is read off this disk, so it answers signed out and offline alike.
        GateNav(NavStorage,       true, "Storage");

        // Needs the server. Signed out is the hard gate; offline only changes the tooltip.
        GateNav(NavSharing, loggedIn, ServerTip("Sharing", "what is shared, and with whom"));

        // The admin flag comes from auth/me and is cached with the account; the server checks it again
        // on every admin route, so this only decides whether the entry is shown.
        NavDev.Visibility = loggedIn && App.State.Settings.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        if (loggedIn && App.State.Settings.UserId is { } userId && _accountReadFor != userId)
        {
            _accountReadFor = userId;
            _ = RefreshAccountDetailsAsync(userId);
        }

        RefreshNavCheckedStates();
        UpdateMcChip();

        string ServerTip(string name, string what) =>
            !loggedIn ? $"{name} - sign in to use this"
            : offline ? $"{name} - offline, so {what} is whatever the server last told us"
            : name;
    }

    /// <summary>The account whose details (name, admin flag) have been read from the server this
    /// session, so a sign-in or a new launch reads them once rather than on every navigation.</summary>
    private Guid? _accountReadFor;

    private async Task RefreshAccountDetailsAsync(Guid userId)
    {
        try
        {
            await App.State.Api.RefreshAccountAsync();
            UpdateChrome();
        }
        catch (Exception ex)
        {
            // Offline or refused: the cached details stand, and the next navigation tries again.
            AppLog.Log("account", "Could not read the account details: " + ex.Message);
            if (_accountReadFor == userId) _accountReadFor = null;
        }
    }

    /// <summary>Enables or disables a nav item and gives it a tooltip explaining why.</summary>
    /// <remarks><see cref="ToolTipService.SetShowOnDisabled"/> matters here: WPF hides a disabled
    /// control's tooltip, and that is when the explanation is needed.</remarks>
    private static void GateNav(ToggleButton nav, bool enabled, string tip)
    {
        nav.IsEnabled = enabled;
        nav.ToolTip   = tip;
        ToolTipService.SetShowOnDisabled(nav, true);
    }

    public void UpdateMcChip()
    {
        var acc = App.State.MinecraftAccounts.Current;
        McAccountChipLabel.Text = acc is null ? "No MC account" : acc.Username;
        McAccountChipLabel.Foreground = acc is null
            ? (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
            : (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");
    }
}
