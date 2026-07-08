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
    private enum MasterPage { None, Packs, Worlds, Mods, ResourcePacks }
    private MasterPage _currentMaster = MasterPage.None;

    // ── side-panel state ─────────────────────────────────────────────────────
    private enum SidePanelKind
    {
        None, Settings, Account, Teams, McAccount, McDefaults,
        PackDetail, ModExplorer, ModManagement, WorldDetail, PackBrowser, WorldBrowser, ModDetail,
        ResourcePackDetail, LocalResourcePackDetail, ResourcePackBrowser, ResourcePackExplorer
    }
    private SidePanelKind _currentSidePanel = SidePanelKind.None;
    private string _currentSidePanelTitle = "";
    private readonly Stack<(SidePanelKind kind, string title, Page page)> _sideStack = new();
    // Default side-panel fraction of the content area when first opened.
    private const double SidePanelDefaultFraction = 0.45;
    private const double SidePanelMinPx = 309;
    private const double SidePanelMaxFraction = 0.772;
    private HwndSource? _source;

    // ── sidebar state ────────────────────────────────────────────────────────
    // Starts collapsed (icon-only). Toggle button expands to 240px.
    private bool _sidebarCollapsed = true;

    private static readonly string[] NavButtonNames =
        { "NavPacks", "NavWorlds", "NavMods", "NavResourcePacks", "NavTeams", "NavAccount", "NavSettings", "NavDev" };

    private static readonly string[] NavLabelNames =
        { "NavPacksLabel", "NavWorldsLabel", "NavModsLabel", "NavResourcePacksLabel",
          "NavTeamsLabel", "NavAccountLabel", "NavSettingsLabel", "NavDevLabel" };

    public MainWindow()
    {
        InitializeComponent();
        Loaded    += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        SizeChanged   += (_, _) => LayoutSidePanel();
        StateChanged  += (_, _) => UpdateMaximizeIcon();
    }

    // ── lifecycle ────────────────────────────────────────────────────────────

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.State.SessionExpired += () => Dispatcher.Invoke(() =>
        {
            MessageBox.Show(this,
                "Your session has expired. Please sign in again.",
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
        if (App.State.Settings.IsLoggedIn)
            NavigateToPacks();
        else
            NavigateToLogin();

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
        catch { /* frozen chrome — caption buttons still work via IsHitTestVisibleInChrome */ }
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
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (WindowWorkArea.TryHandleGetMinMaxInfo(hwnd, msg, lParam))
            handled = true;

        return IntPtr.Zero;
    }

    // ── master navigation ────────────────────────────────────────────────────

    public void NavigateToLogin()
    {
        ResetSidePanel();
        Sidebar.IsEnabled = false;
        MainFrame.Navigate(new LoginView(this));
        _currentMaster = MasterPage.None;
        UpdateChrome();
    }

    public void NavigateToPacks()
    {
        Sidebar.IsEnabled = true;
        ResetSidePanel();
        if (MainFrame.Content is not PackListView)
            MainFrame.Navigate(new PackListView(this));
        _currentMaster = MasterPage.Packs;
        UpdateChrome();
        // Pull the admin-managed global default rules so this launcher seeds new
        // packs with the same routing every other launcher uses. Fire-and-forget —
        // failures fall back to the local cache.
        _ = App.State.Rules.SyncGlobalDefaultsFromServerAsync();
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

    public void NavigateToWorlds()
    {
        Sidebar.IsEnabled = true;
        ResetSidePanel();
        if (MainFrame.Content is not WorldsView)
            MainFrame.Navigate(new WorldsView(this));
        _currentMaster = MasterPage.Worlds;
        UpdateChrome();
    }

    public void NavigateToMods()
    {
        Sidebar.IsEnabled = true;
        ResetSidePanel();
        if (MainFrame.Content is not ModsView)
            MainFrame.Navigate(new ModsView(this));
        _currentMaster = MasterPage.Mods;
        UpdateChrome();
    }

    public void NavigateToResourcePacks()
    {
        Sidebar.IsEnabled = true;
        ResetSidePanel();
        if (MainFrame.Content is not ResourcePacksView)
            MainFrame.Navigate(new ResourcePacksView(this));
        _currentMaster = MasterPage.ResourcePacks;
        UpdateChrome();
    }

    // ── side-panel openers ───────────────────────────────────────────────────

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

    public void OpenModExplorerForPack(CloudLauncher.Shared.PackDetail pack) =>
        OpenSidePanelPushed(SidePanelKind.ModExplorer, $"Browse mods · {pack.Name}", new ModExplorerPage(this, pack));

    public void OpenModManagementForPack(CloudLauncher.Shared.PackDetail pack) =>
        OpenSidePanelPushed(SidePanelKind.ModManagement, $"Mods · {pack.Name}", new ModManagementView(this, pack));

    public void OpenPackBrowser() =>
        OpenSidePanelFresh(SidePanelKind.PackBrowser, "Download a pack", new PackBrowserView(this));

    public void OpenWorldBrowser() =>
        OpenSidePanelFresh(SidePanelKind.WorldBrowser, "Download a world", new WorldBrowserView(this));

    public void OpenResourcePackDetail(Guid packId, string title) =>
        OpenSidePanelPushed(SidePanelKind.ResourcePackDetail, title,
            new ResourcePackDetailView(this, packId));

    public void OpenLocalResourcePackDetail(string key, string title) =>
        OpenSidePanelPushed(SidePanelKind.LocalResourcePackDetail, title,
            new LocalResourcePackDetailView(this, key));

    public void OpenResourcePackBrowser() =>
        OpenSidePanelFresh(SidePanelKind.ResourcePackBrowser, "Download resource packs",
            new ResourcePackBrowserView(this));

    public void OpenResourcePackExplorerForPack(CloudLauncher.Shared.PackDetail pack) =>
        OpenSidePanelPushed(SidePanelKind.ResourcePackExplorer,
            $"Browse resource packs · {pack.Name}",
            new ResourcePackExplorerPage(this, pack));

    public void OpenMinecraftHost(CloudLauncher.Shared.PackDetail pack, System.Diagnostics.Process process)
    {
        // When the custom game window is turned off, Minecraft runs in its own native
        // window. The launcher already started the process and still tracks it (play
        // time, instance state), so there is nothing to host — just let the game show
        // its own window.
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
        // Was the panel closed before this call? If so, play the slide-in on the host.
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

        // Side panel is open — restore minimum so the user can't drag it below 280 px.
        SidePanelColumn.MinWidth = SidePanelMinPx;

        // The mod explorer is a full browsing experience (two-column list + detail),
        // so always present it full-width like the standalone mod browser — even when
        // side-by-side panels are enabled.
        var forceCover = _currentSidePanel is SidePanelKind.ModExplorer or SidePanelKind.ModManagement;
        var useSideBySide = App.State.Settings.UseSidePanel && !forceCover;

        if (!useSideBySide)
        {
            // Cover mode — side panel takes the whole content area, master is hidden.
            // Clear the master column's MinWidth too: a column's MinWidth overrides its
            // Width, so without this the 360-px minimum keeps an empty band on the left
            // (the collapsed MainFrame) instead of letting the side panel fill the screen.
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

        // Side-by-side mode (default) — restore the master column's readable minimum.
        MainColumn.MinWidth = 360;
        MainFrame.Visibility = Visibility.Visible;

        // Only set the initial split when the side panel is transitioning from closed.
        // After the user drags the splitter we leave their ratio intact.
        if (SidePanelColumn.Width.Value == 0 || SidePanelColumn.Width.GridUnitType == GridUnitType.Pixel)
        {
            // ActualWidth is in window units; the grid (and its columns) live inside RootScale, so
            // convert to the scaled grid's coordinates before mixing with column widths.
            var contentPx = ActualWidth / Math.Max(RootScale.ScaleX, 0.1) - SidebarColumn.ActualWidth;
            if (contentPx < 100) contentPx = 1000;
            var panelPx = Math.Max(
                Math.Min(contentPx * SidePanelDefaultFraction, contentPx * SidePanelMaxFraction),
                SidePanelMinPx);
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
        DialogLayer.Visibility = Visibility.Visible;
        try { return await overlay.Result; }
        finally
        {
            DialogLayer.Children.Remove(overlay);
            if (DialogLayer.Children.Count == 0) DialogLayer.Visibility = Visibility.Collapsed;
        }
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
    public async Task ShowCardAsync(FrameworkElement card, Task completion, Action? onBackdropCancel = null)
    {
        var layer = new Grid();
        var backdrop = new Border { Background = OverlayBackdrop };
        if (onBackdropCancel is not null)
            backdrop.MouseLeftButtonDown += (_, _) => onBackdropCancel();
        layer.Children.Add(backdrop);

        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Center;
        layer.Children.Add(card);

        DialogLayer.Children.Add(layer);
        DialogLayer.Visibility = Visibility.Visible;
        try { await completion; }
        finally
        {
            DialogLayer.Children.Remove(layer);
            if (DialogLayer.Children.Count == 0) DialogLayer.Visibility = Visibility.Collapsed;
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

    /// <summary>
    /// Apply the current _sidebarCollapsed flag visually.
    /// Layout-wise, every nav row already uses a fixed 60-px icon column + flex label column,
    /// so we only need to show/hide the labels — icons stay anchored automatically.
    /// </summary>
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

    private void OnNavPacks(object sender, RoutedEventArgs e)  => NavigateToPacks();
    private void OnNavWorlds(object sender, RoutedEventArgs e) => NavigateToWorlds();
    private void OnNavMods(object sender, RoutedEventArgs e)           => NavigateToMods();
    private void OnNavResourcePacks(object sender, RoutedEventArgs e) => NavigateToResourcePacks();

    private void OnNavTeams(object sender, RoutedEventArgs e)    => ToggleTopLevel(SidePanelKind.Teams,    OpenTeams);
    private void OnNavAccount(object sender, RoutedEventArgs e)  => ToggleTopLevel(SidePanelKind.Account,  OpenAccount);
    private void OnNavSettings(object sender, RoutedEventArgs e) => ToggleTopLevel(SidePanelKind.Settings, OpenSettings);

    private void OnNavDev(object sender, RoutedEventArgs e)       => new DevWindow { Owner = this }.ShowDialog();
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
        var add = new System.Windows.Controls.MenuItem { Header = "Add or manage accounts…" };
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
        NavPacks.IsChecked    = _currentMaster == MasterPage.Packs;
        NavWorlds.IsChecked   = _currentMaster == MasterPage.Worlds;
        NavMods.IsChecked           = _currentMaster == MasterPage.Mods;
        NavResourcePacks.IsChecked = _currentMaster == MasterPage.ResourcePacks;
        NavTeams.IsChecked    = _currentSidePanel == SidePanelKind.Teams;
        NavAccount.IsChecked  = _currentSidePanel == SidePanelKind.Account
                             || _currentSidePanel == SidePanelKind.McAccount;
        NavSettings.IsChecked = _currentSidePanel == SidePanelKind.Settings;
    }

    // ── chrome / user state ──────────────────────────────────────────────────

    public void UpdateChrome()
    {
        var loggedIn = App.State.Settings.IsLoggedIn;
        var username = App.State.Settings.Username ?? "";

        NavAccountLabel.Text  = loggedIn ? username : "Sign in";
        NavTeams.IsEnabled    = loggedIn;
        NavWorlds.IsEnabled   = loggedIn;
        NavMods.IsEnabled           = loggedIn;
        NavResourcePacks.IsEnabled = loggedIn;

        NavDev.Visibility = (loggedIn && string.Equals(username, "colud", StringComparison.OrdinalIgnoreCase))
            ? Visibility.Visible : Visibility.Collapsed;

        RefreshNavCheckedStates();
        UpdateMcChip();
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
