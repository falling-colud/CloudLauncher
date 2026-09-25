using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class PackDetailView : Page
{
    private readonly MainWindow _shell;
    private readonly Guid _packId;
    private readonly bool _hostedInMinecraftWindow;

    /// <summary>The instance this page is showing. Used by the shell to close the page if the
    /// instance is deleted.</summary>
    public Guid PackId => _packId;

    private PackDetail? _pack;
    private bool _isOwner;
    private bool _suppressEvents;
    private bool _fileTransferInProgress;

    /// <summary>Set while an upload or download is running, so the same button can cancel it.</summary>
    private CancellationTokenSource? _syncCts;

    /// <summary>Cancels the background thumbnail pass when the tab is refreshed or the
    /// page closes.</summary>
    private CancellationTokenSource? _screenshotCts;

    /// <summary>Cancels the Files tab's summary walk, so a refresh during a long walk does not leave
    /// two walks racing to write the same label.</summary>
    private CancellationTokenSource? _fileSummaryCts;

    private readonly DispatcherTimer _autoApplyTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _overviewSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly ObservableCollection<WorldRow> _worldRows = new();
    private readonly ObservableCollection<PackScreenshotRow> _packScreenshotRows = new();
    private static readonly string[] ScreenshotExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];
    private static readonly Regex OverviewLinkRegex = new(
        @"\b((?:https?://|www\.)[^\s<>()]+|mailto:[^\s<>()]+|[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private string _lastSavedSummary = "";
    private string _lastSavedDescription = "";
    private bool _isSavingOverview;
    private bool _overviewSavePending;
    private bool _showRichDescription;
    private bool _importedRichDescription;
    private bool _customFormattedDescription;
    private bool _descriptionEditing;
    private string _currentDescriptionHtml = "";
    private string _lastSavedDescriptionHtml = "";

    private enum DetailTabKind { Overview, Mods, ResourcePacks, Files, Screenshots, Worlds, Options, Logs }
    private DetailTabKind _currentTab = DetailTabKind.Overview;
    private readonly HashSet<DetailTabKind> _tabsLoaded = new();

    /// <summary>The record the page last drew, as JSON, so a refresh that brings back the same answer
    /// draws nothing again. Null until something has been drawn.</summary>
    private string? _appliedSnapshot;

    public event EventHandler<double>? HostPanelWidthRequested;

    private bool _heroNarrow;
    private readonly List<Button> _fileActionButtons = new();

    /// <summary>
    /// Puts the file actions (Export, Open folder, Manage files) in a column beside Play when there is
    /// room, otherwise into Play's wrapping row with the column hidden.
    /// </summary>
    /// <remarks>Measured from the buttons' desired sizes rather than a fixed width, so a wider font or
    /// an extra chip in the Play row moves the breakpoint too.</remarks>
    private void LayoutHeroActions()
    {
        if (_fileActionButtons.Count == 0)
            _fileActionButtons.AddRange(FileActionsStack.Children.OfType<Button>());

        var width = HeroActionsGrid.ActualWidth;
        if (width <= 0) return;
        var playRow = HeroPlayRow.Children.OfType<FrameworkElement>()
            .Where(c => c.Visibility == Visibility.Visible && !(c is Button b && _fileActionButtons.Contains(b)))
            .Sum(c => c.DesiredSize.Width);
        var column = _fileActionButtons.Max(b => b.DesiredSize.Width) + 16;
        var narrow = width < playRow + column;
        if (narrow == _heroNarrow) return;
        _heroNarrow = narrow;

        foreach (var b in _fileActionButtons)
            (b.Parent as Panel)?.Children.Remove(b);

        if (narrow)
        {
            foreach (var b in _fileActionButtons)
            {
                b.Margin = new Thickness(0, 0, 10, 10);
                HeroPlayRow.Children.Add(b);
            }
            FileActionsStack.Visibility = Visibility.Collapsed;
        }
        else
        {
            for (var i = 0; i < _fileActionButtons.Count; i++)
            {
                _fileActionButtons[i].Margin = new Thickness(0, 0, 0, i == _fileActionButtons.Count - 1 ? 10 : 6);
                FileActionsStack.Children.Add(_fileActionButtons[i]);
            }
            FileActionsStack.Visibility = Visibility.Visible;
        }
    }

    public PackDetailView(MainWindow shell, Guid packId, bool hostedInMinecraftWindow = false)
    {
        InitializeComponent();
        _shell = shell;
        _packId = packId;
        _hostedInMinecraftWindow = hostedInMinecraftWindow;
        _autoApplyTimer.Tick += OnAutoApplyTick;
        _overviewSaveTimer.Tick += OnOverviewSaveTimerTick;
        HeroActionsGrid.SizeChanged += (_, _) => LayoutHeroActions();
        WireOverviewTextBox(OverviewSummaryBox);
        WorldsList.ItemsSource = _worldRows;
        PackScreenshotList.ItemsSource = _packScreenshotRows;
        GameView.FilesDropped   += OnFilesDropped;
        GameView.FileActivated  += OpenInEditor;   // double-click a file -> built-in editor
        SharedView.FilesDropped += OnFilesDropped;
        SharedView.FileActivated += OpenInEditor;
        // Files dropped in from Explorer land in whichever folder is open, like on the Mods screen.
        GameView.ExternalFilesDropped   += (root, dir, files) => OnExternalFilesDropped(GameView, root, dir, files);
        SharedView.ExternalFilesDropped += (root, dir, files) => OnExternalFilesDropped(SharedView, root, dir, files);
        GameView.DeleteRequested   += () => _ = DeleteSelectedAsync(GameView);
        GameView.RenameRequested   += () => _ = RenameSelectedAsync(GameView);
        SharedView.DeleteRequested += () => _ = DeleteSelectedAsync(SharedView);
        SharedView.RenameRequested += () => _ = RenameSelectedAsync(SharedView);
        LogContent.StatsChanged += UpdateLogLinesLabel;
        ProgressHub.ProgressChanged += OnHeroProgressChanged;
        ProgressHub.ProgressCleared += OnHeroProgressCleared;
        PackJobs.Changed += OnPackJobsChanged;
        App.State.Instances.StateChanged += OnInstanceStateChanged;
        ServerHostingPanel.Host.StateChanged += OnServerHostStateChanged;
        App.State.ModpackDownload.PackAdded += OnPackDownloadUpdated;
        AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPagePreviewMouseWheel), true);
        AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPagePreviewKeyDown), true);
        Loaded += async (_, _) => await ReloadAsync();
        if (_hostedInMinecraftWindow)
        {
            DetailTabs.HorizontalAlignment = HorizontalAlignment.Left;
            DetailTabs.ItemContainerStyle = CreateHostedTabItemStyle();
            DetailTabs.SizeChanged += (_, _) => NotifyHostPanelWidth();
            // Hidden from the first frame, not only once the record arrives (see ApplyHostedTabChrome).
            ExportPackButton.Visibility = Visibility.Collapsed;
        }
        Unloaded += async (_, _) =>
        {
            _autoApplyTimer.Stop();
            _overviewSaveTimer.Stop();
            // Stop uploads and thumbnail passes that would report into a closed view. Guarded
            // because this handler is async void. PackJobs keep running: a download belongs to the
            // instance, not the page.
            try { _syncCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _screenshotCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _fileSummaryCts?.Cancel(); } catch (ObjectDisposedException) { }
            // Unsubscribe before anything that can throw or await, or a failed save below would
            // leak the whole view through these static events.
            ProgressHub.ProgressChanged -= OnHeroProgressChanged;
            ProgressHub.ProgressCleared -= OnHeroProgressCleared;
            PackJobs.Changed -= OnPackJobsChanged;
            App.State.Instances.StateChanged -= OnInstanceStateChanged;
            ServerHostingPanel.Host.StateChanged -= OnServerHostStateChanged;
            App.State.ModpackDownload.PackAdded -= OnPackDownloadUpdated;
            // The Logs tab subscribes this when "Launcher log" is selected; remove it in case the page
            // closes on that row.
            AppLog.MessageAppended -= OnLauncherLogAppended;
            if (_pack is not null && _isOwner)
                await SaveOverviewDescriptionAsync();
        };

        PaintRemembered();
    }

    /// <summary>Draws what this PC last knew about the instance before the page is shown, so it
    /// does not slide in empty and then jump when the server answers.</summary>
    /// <remarks>The record comes from <see cref="ApiClient.PeekPackDetail"/>: the last server
    /// answer, else one built from the instance list. <see cref="ReloadAsync"/> still asks the
    /// server and redraws only if the answer differs.</remarks>
    private void PaintRemembered()
    {
        if (App.State.Api.PeekPackDetail(_packId) is not { } remembered) return;
        try
        {
            _pack = remembered;
            _isOwner = remembered.OwnerId == App.State.Settings.UserId;
            // The server path does this first too: opening the instance page creates its folder.
            App.State.Packs.EnsurePackFolder(_packId, remembered.Name, remembered.IsShared);
            ApplyPack(remembered);
            ResetTabLoadState();
            _appliedSnapshot = Snapshot(remembered);
        }
        catch (Exception ex)
        {
            // Only the head start is lost; the server's answer still follows.
            AppLog.LogError("packs.remembered-detail", ex);
            _pack = null;
            _appliedSnapshot = null;
        }
    }

    private static string Snapshot(PackDetail pack) => System.Text.Json.JsonSerializer.Serialize(pack);

    private void OnHeroProgressChanged(ProgressInfo info)
    {
        if (info.PackId != _packId) return;
        ProgressArea.Visibility = Visibility.Visible;
        ProgressLabel.Text = info.Label;
        if (info.Fraction < 0)
        {
            DetailProgressBar.IsIndeterminate = true;
            ProgressPercent.Text = "";
        }
        else
        {
            DetailProgressBar.IsIndeterminate = false;
            DetailProgressBar.Value = info.Fraction * 100;
            ProgressPercent.Text = $"{(int)(info.Fraction * 100)}%";
        }
        UpdateCurrentProgress(info);
    }

    private void OnHeroProgressCleared(Guid packId)
    {
        if (packId != _packId) return;
        ProgressArea.Visibility = Visibility.Collapsed;
        DetailProgressBar.IsIndeterminate = false;
        DetailProgressBar.Value = 0;
        DetailCurrentProgressBar.IsIndeterminate = false;
        DetailCurrentProgressBar.Value = 0;
        DetailCurrentProgressBar.Visibility = Visibility.Collapsed;
        CurrentProgressHeader.Visibility = Visibility.Collapsed;
        CurrentProgressLabel.Text = "";
        CurrentProgressPercent.Text = "";
        ProgressPercent.Text = "";
        ProgressLabel.Text = "";
    }

    private void UpdateCurrentProgress(ProgressInfo info)
    {
        if (!info.HasCurrentProgress)
        {
            DetailCurrentProgressBar.IsIndeterminate = false;
            DetailCurrentProgressBar.Value = 0;
            DetailCurrentProgressBar.Visibility = Visibility.Collapsed;
            CurrentProgressHeader.Visibility = Visibility.Collapsed;
            CurrentProgressLabel.Text = "";
            CurrentProgressPercent.Text = "";
            return;
        }

        CurrentProgressHeader.Visibility = Visibility.Visible;
        DetailCurrentProgressBar.Visibility = Visibility.Visible;
        CurrentProgressLabel.Text = string.IsNullOrWhiteSpace(info.CurrentLabel)
            ? "Current stage"
            : info.CurrentLabel;

        if (info.CurrentFraction < 0)
        {
            DetailCurrentProgressBar.IsIndeterminate = true;
            CurrentProgressPercent.Text = "";
        }
        else
        {
            DetailCurrentProgressBar.IsIndeterminate = false;
            DetailCurrentProgressBar.Value = info.CurrentFraction * 100;
            CurrentProgressPercent.Text = $"{(int)(info.CurrentFraction * 100)}%";
        }
    }

    /// <summary>F5 reloads the current tab and Ctrl+F focuses its search box, as on the other list
    /// screens.</summary>
    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            EnsureTabLoaded(_currentTab, force: true);
            e.Handled = true;
            return;
        }

        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;

        switch (_currentTab)
        {
            case DetailTabKind.Logs:
                LogFilterBox.Focus();
                LogFilterBox.SelectAll();
                e.Handled = true;
                break;
            case DetailTabKind.Files:
                GameView.FocusFilter();
                e.Handled = true;
                break;
        }
    }

    private void OnInstanceStateChanged(Guid packId)
    {
        if (packId != _packId) return;
        UpdateLaunchButtonState();
    }

    // ── the header's server chip ─────────────────────────────────────────────

    private void OnServerHostStateChanged(Guid packId)
    {
        if (packId != _packId) return;
        if (Dispatcher.CheckAccess()) UpdateServerChip();
        else Dispatcher.BeginInvoke(UpdateServerChip);
    }

    /// <summary>Shows whether this instance has a server running. The chip opens the Hosting tab and is
    /// collapsed when nothing is running.</summary>
    private void UpdateServerChip()
    {
        // Must watch the same tracker the Hosting tab starts servers on, which lives on
        // ServerHostingPanel.Host for now.
        var state = ServerHostingPanel.Host.GetStatus(_packId);
        if (!state.IsLive && state.Status != ServerStatus.Crashed)
        {
            ServerStatusChip.Visibility = Visibility.Collapsed;
            return;
        }

        ServerStatusChip.Visibility = Visibility.Visible;
        var (text, brushKey, tip) = state.Status switch
        {
            ServerStatus.Installing => ("Server installing...", "WarningBrush",
                "This instance's server is being prepared. Open the Hosting tab for the log."),
            ServerStatus.Starting => ("Server starting...", "WarningBrush",
                "This instance's server is loading. Open the Hosting tab for its console."),
            ServerStatus.Stopping => ("Server stopping...", "WarningBrush",
                "This instance's server is saving and shutting down. Open the Hosting tab to watch."),
            ServerStatus.Crashed => ("Server crashed", "DangerBrush",
                "This instance's server exited on its own. Open the Hosting tab for the last lines."),
            _ => ($"Server running · port {state.Port}", "SuccessBrush",
                "This instance's server is accepting players. Open the Hosting tab for its console."),
        };

        ServerStatusChipText.Text = text;
        ServerStatusChipGlyph.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

        // The Hosting tab is on the file-management page, which needs the record and the launcher
        // window. Otherwise the chip still shows the state, and the tooltip says why it cannot open it.
        var canOpen = _pack is not null && !_hostedInMinecraftWindow;
        ServerStatusChip.IsEnabled = canOpen;
        ToolTipService.SetShowOnDisabled(ServerStatusChip, true);
        ServerStatusChip.ToolTip = canOpen
            ? tip
            : _hostedInMinecraftWindow
                ? text + ". The Hosting tab is on the launcher window, behind the game."
                : tip + " This instance's details have not been downloaded yet, so the page cannot "
                      + "be opened until the server answers.";
    }

    /// <summary>The chip's click, and the only route from this page to the Hosting tab.</summary>
    private void OnOpenServerHosting(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        FileManagementView.OpenOn(_shell, _pack, FileManagementTab.Hosting);
    }

    private void OnPackDownloadUpdated(PackSummary pack)
    {
        if (pack.Id != _packId) return;
        Dispatcher.BeginInvoke(async () => await ReloadAsync());
    }

    private void UpdateLaunchButtonState()
    {
        // Play is hidden while a transfer holds its slot, so refresh the transfer controls too.
        UpdateTransferControls();
        var playable = _pack is { IsEmpty: false } && !string.IsNullOrEmpty(_pack.MinecraftVersion);
        var status = App.State.Instances.GetStatus(_packId);
        if (status != MinecraftInstanceStatus.Idle)
        {
            LaunchButton.Style = (Style)FindResource("KillInstanceButton");
            LaunchButtonIcon.Text = "\uE8BB";
            LaunchButtonText.Text = "Kill instance";
            LaunchButton.ToolTip = status == MinecraftInstanceStatus.Launching
                ? "Cancel launch and stop any started Minecraft process"
                : "Kill the running Minecraft instance";
            LaunchButton.IsEnabled = true;
            return;
        }

        LaunchButton.Style = (Style)FindResource("PlayButton");
        LaunchButtonIcon.Text = "\uE768";
        LaunchButtonText.Text = "Play";
        LaunchButton.ToolTip = null;
        LaunchButton.IsEnabled = playable;
    }

    // ── transfer controls (pause / stop) ─────────────────────────────────────

    private void OnPackJobsChanged(Guid packId)
    {
        if (packId != _packId) return;
        if (Dispatcher.CheckAccess()) UpdateTransferControls();
        else Dispatcher.BeginInvoke(UpdateTransferControls);
    }

    /// <summary>While a transfer is running for this instance, Pause and Stop replace Play.</summary>
    private void UpdateTransferControls()
    {
        // An export only reads the instance, so it leaves Play alone (the game can run meanwhile
        // and the export card has its own Cancel). It still blocks Update, which would rewrite
        // the files being packed.
        var found = PackJobs.For(_packId);
        if (found is { Kind: PackJobKind.Export })
        {
            PauseJobButton.Visibility = Visibility.Collapsed;
            StopJobButton.Visibility = Visibility.Collapsed;
            LaunchButton.Visibility = Visibility.Visible;
            if (_pack is not null)
            {
                UpdateButtonText.Text = "Update available";
                UpdateButton.IsEnabled = false;
                UpdateButton.ToolTip = "An export is reading this instance. Update it once that has finished.";
            }
            return;
        }

        var job = found;
        var running = job is not null;

        PauseJobButton.Visibility = running && !job!.IsStopping ? Visibility.Visible : Visibility.Collapsed;
        StopJobButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        LaunchButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

        if (job is null)
        {
            // Offer the update again, unless an upload is running (tracked by _syncCts, not a job).
            if (_pack is not null)
            {
                UpdateButtonText.Text = "Update available";
                UpdateButton.IsEnabled = _syncCts is null;
                UpdateButton.ToolTip = "Download the latest version from the server";
            }
            return;
        }

        var paused = job.IsPaused;
        PauseJobIcon.Text = paused ? "\uE768" : "\uE769";      // play / pause
        PauseJobText.Text = paused ? "Resume" : "Pause";
        PauseJobButton.ToolTip = paused
            ? $"Carry on with the {job.KindLabel}"
            : $"Hold the {job.KindLabel} where it is - nothing already downloaded is lost";
        StopJobText.Text = job.IsStopping ? "Stopping..." : "Stop";
        StopJobButton.IsEnabled = !job.IsStopping;

        // The transfer may have been started from the instance list, where SetSyncUiRunning never ran
        // for this page, so lock the Update button here.
        UpdateButtonText.Text = "Updating...";
        UpdateButton.IsEnabled = false;
        UpdateButton.ToolTip = $"Already {job.KindLabel}ing - use Pause or Stop to interrupt it";
        StopJobButton.ToolTip =
            $"Stop the {job.KindLabel} and delete the files it downloaded. Files that were already here stay.";
    }

    private void OnPauseJob(object sender, RoutedEventArgs e)
    {
        var job = PackJobs.For(_packId);
        if (job is null) return;

        if (job.IsPaused)
        {
            job.Resume();
            StatusLabel.Text = $"Resumed the {job.KindLabel}.";
        }
        else
        {
            job.Pause();
            // Say it is paused, or a still bar looks like a stuck transfer.
            StatusLabel.Text = $"Paused - the {job.KindLabel} is holding where it is. Press Resume to carry on.";
        }
    }

    private void OnStopJob(object sender, RoutedEventArgs e)
    {
        var job = PackJobs.For(_packId);
        if (job is null) return;
        StatusLabel.Text = $"Stopping the {job.KindLabel}...";
        job.Stop();
    }

    /// <summary>
    /// Drop handler for the FolderViews. A drop onto SharedView marks the files as shared with a rule
    /// instead of moving them, since they are already in game/. Other drops move or copy as normal.
    /// </summary>
    private async void OnFilesDropped(string sourceRoot, string destRoot, IReadOnlyList<string> relativePaths)
    {
        if (_fileTransferInProgress) return;

        var gameDir = App.State.Packs.GameDir(_packId);
        var droppingOntoSyncView =
            string.Equals(destRoot, gameDir, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(sourceRoot, gameDir, StringComparison.OrdinalIgnoreCase);

        if (droppingOntoSyncView)
        {
            if (!CanEditSharing)
            {
                StatusLabel.Text = "Only the instance owner and collaborators with upload rights "
                                 + "can change what this instance shares.";
                return;
            }

            // Mark the dropped files as shared by adding rules.
            var packRoot = App.State.Packs.PackRoot(_packId);
            var rules = App.State.Rules.Load(packRoot);
            foreach (var rel in relativePaths)
            {
                // Folders need a trailing-slash pattern so the matcher covers their contents
                // (see AddQuickRule). Detect a directory on disk and normalise the pattern.
                var isDir = Directory.Exists(Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                var pattern = isDir ? rel.TrimEnd('/') + "/" : rel;
                rules.RemoveAll(r => string.Equals(r.Pattern.TrimEnd('/'), pattern.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
                rules.Insert(0, new PackRule { Pattern = pattern, Action = Services.RuleAction.Shared });
            }
            App.State.Rules.Save(packRoot, rules);
            StatusLabel.Text = $"Marked {relativePaths.Count} file(s) as shared.";
            await RefreshFileListsAsync(runAutoApply: false);
            return;
        }

        await TransferEntriesAsync(sourceRoot, destRoot, relativePaths);
    }

    // ── tab lazy-load ────────────────────────────────────────────────────────

    private void ApplyHostedTabChrome()
    {
        if (!_hostedInMinecraftWindow)
            return;

        // The host window's side strip has no room for the file browser, and File Management would open
        // behind the game. The header's Edit files button still opens the standalone editor.
        FilesTab.Visibility = Visibility.Collapsed;
        OptionsTab.Visibility = Visibility.Collapsed;

        // The Overview tab is read-only inside the Minecraft host window.
        OverviewSummaryBox.IsReadOnly = true;
        OverviewSaveStatusLabel.Visibility = Visibility.Collapsed;
        OverviewSummaryCountLabel.Visibility = Visibility.Collapsed;
        OverviewCharacterCountLabel.Visibility = Visibility.Collapsed;

        // Keep the mods button, but the hub would open behind the game, so relabel it to match what
        // OnOpenModHub does here.
        OverviewModHubGlyph.Text = "\uE721";
        OverviewModHubText.Text = "Browse mods";
        OverviewModHubButton.ToolTip =
            "Find and install mods for this instance without leaving the game. The full Modpack "
            + "Management page is on the launcher window, behind the game.";

        // The export card opens on the launcher window, which is behind the game here.
        ExportPackButton.Visibility = Visibility.Collapsed;

        if (_currentTab is DetailTabKind.Files or DetailTabKind.Options)
            SelectOverviewTab();

        NotifyHostPanelWidth();
    }

    private void SelectOverviewTab()
    {
        _currentTab = DetailTabKind.Overview;
        foreach (TabItem item in DetailTabs.Items)
        {
            if (item.Header?.ToString() != "Overview")
                continue;
            DetailTabs.SelectedItem = item;
            break;
        }
    }

    private Style CreateHostedTabItemStyle()
    {
        var headerTemplate = new DataTemplate();
        var headerText = new FrameworkElementFactory(typeof(TextBlock));
        headerText.SetBinding(TextBlock.TextProperty, new Binding());
        headerText.SetValue(TextBlock.TextWrappingProperty, TextWrapping.NoWrap);
        headerTemplate.VisualTree = headerText;

        var style = new Style(typeof(TabItem), (Style)FindResource(typeof(TabItem)));
        style.Setters.Add(new Setter(TabItem.HeaderTemplateProperty, headerTemplate));
        return style;
    }

    private void NotifyHostPanelWidth()
    {
        if (!_hostedInMinecraftWindow)
            return;

        PublishHostPanelWidth();
        Dispatcher.BeginInvoke(PublishHostPanelWidth, DispatcherPriority.Loaded);
    }

    private void PublishHostPanelWidth()
    {
        if (!_hostedInMinecraftWindow)
            return;

        var tabStripWidth = MeasureTabStripWidth();
        DetailTabs.MinWidth = tabStripWidth;
        HostPanelWidthRequested?.Invoke(this, tabStripWidth + 48); // DetailTabs left + right margin
    }

    private double MeasureTabStripWidth()
    {
        DetailTabs.ApplyTemplate();
        if (DetailTabs.Template.FindName("HeaderPanel", DetailTabs) is Panel headerPanel)
        {
            headerPanel.UpdateLayout();
            if (headerPanel.ActualWidth > 0)
                return headerPanel.ActualWidth;
        }

        var tabStripWidth = 0.0;
        foreach (TabItem item in DetailTabs.Items)
        {
            if (item.Visibility != Visibility.Visible)
                continue;

            item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tabStripWidth += item.DesiredSize.Width;
        }

        return tabStripWidth;
    }

    private void ResetTabLoadState()
    {
        _tabsLoaded.Clear();
        _tabsLoaded.Add(DetailTabKind.Overview);
    }

    private void OnDetailTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != DetailTabs || DetailTabs.SelectedItem is not TabItem tab) return;
        _currentTab = TabKindFromHeader(tab.Header?.ToString());
        UpdateRichDescriptionChrome();
        EnsureTabLoaded(_currentTab);
    }

    private static DetailTabKind TabKindFromHeader(string? header) => header switch
    {
        "Mods" => DetailTabKind.Mods,
        "Resources" => DetailTabKind.ResourcePacks,
        "Files" => DetailTabKind.Files,
        "Screenshots" => DetailTabKind.Screenshots,
        "Worlds" => DetailTabKind.Worlds,
        "Options" => DetailTabKind.Options,
        "Logs" => DetailTabKind.Logs,
        _ => DetailTabKind.Overview
    };

    private Window ListOwnerWindow =>
        _hostedInMinecraftWindow ? Window.GetWindow(this) ?? _shell : _shell;

    private void EnsureTabLoaded(DetailTabKind tab, bool force = false)
    {
        // The Files tab only needs the local folder; every other tab needs the instance record.
        if (_pack is null && tab != DetailTabKind.Files) return;
        if (force)
            _tabsLoaded.Remove(tab);
        else if (!_tabsLoaded.Add(tab))
            return;

        if (tab == DetailTabKind.Files)
        {
            _ = RefreshFileListsAsync();
            return;
        }

        // Anything past here was gated on the record above.
        var pack = _pack!;

        switch (tab)
        {
            case DetailTabKind.Mods:
                ModListCtrl.Load(pack, ListOwnerWindow);
                break;
            case DetailTabKind.ResourcePacks:
                ResourcePackListCtrl.Load(pack, ListOwnerWindow);
                break;
            case DetailTabKind.Screenshots:
                RefreshPackScreenshots();
                break;
            case DetailTabKind.Worlds:
                RefreshWorlds();
                break;
            case DetailTabKind.Logs:
                RefreshLogsList();
                break;
        }
    }

    // ── reload ───────────────────────────────────────────────────────────────

    private async Task ReloadAsync()
    {
        StatusLabel.Text = "";
        try
        {
            _pack = await App.State.Api.GetPackAsync(_packId);
            _isOwner = _pack.OwnerId == App.State.Settings.UserId;
            App.State.Packs.EnsurePackFolder(_packId, _pack.Name, _pack.IsShared);

            if (_pack.IsShared && _pack.Rules.Count > 0)
                App.State.Rules.SyncFromServer(App.State.Packs.PackRoot(_packId), _pack.Rules);

            // Redraw only when the answer differs from what is already on screen (the remembered record
            // or an earlier answer), since reapplying reloads the description and re-lays the header.
            var snapshot = Snapshot(_pack);
            if (snapshot != _appliedSnapshot)
            {
                ApplyPack(_pack);
                _appliedSnapshot = snapshot;
            }
            ResetTabLoadState();
            if (_currentTab != DetailTabKind.Overview)
                EnsureTabLoaded(_currentTab, force: true);
            AutoLinkToTeamFolders(_pack);
            await TrySyncSharedContentAsync();
            _ = CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex is ApiException { Status: HttpStatusCode.NotFound }
                ? "This instance was removed or is no longer available."
                : ex.Message;
            // GetPackAsync already falls back to the offline cache, so there is no record at all.
            // The folder is still here though, and the Files tab only needs the id.
            ApplyRecordlessFileAccess();
        }
    }

    /// <summary>
    /// Leaves only local file access (browse, edit, rename, delete) for an instance with no record.
    /// Everything that needs the server is disabled with a reason in its tooltip.
    /// </summary>
    private void ApplyRecordlessFileAccess()
    {
        // PackRoot throws when there is no folder and no name to create one, and then there is nothing
        // to offer. This runs inside ReloadAsync's catch, so swallow rather than escape an async void.
        string? root;
        try { root = App.State.Packs.PackRoot(_packId); }
        catch (Exception ex)
        {
            AppLog.LogError("packs.recordless", ex);
            return;
        }

        PackRootHint.Text = "Instance root: " + root;
        FilesTab.Visibility = Visibility.Visible;
        SharingOptionsPanel.Visibility = Visibility.Collapsed;
        ApplyHostedTabChrome();
        UpdateServerChip();

        // The file-management page needs the record, so without one this falls back to the editor
        // window, like the header's Edit files button.
        OpenFileManagementButton.IsEnabled = true;
        OpenFileManagementButton.ToolTip =
            "Opens the file editor. The full page needs this instance's details from CloudLauncher, "
            + "which have not been downloaded yet.";

        // Everything that needs the server is disabled, with the reason in the tooltip.
        DisableWithReason(UploadButton, "Uploading needs this instance's details from CloudLauncher.");
        DisableWithReason(ApplyRulesButton, "Rules need this instance's details from CloudLauncher.");
        DisableWithReason(ExportPackButton,
            "Exporting needs this instance's Minecraft version and loader, which come with its details "
            + "from CloudLauncher.");
        ApplySharingPermissionsToFilesTab();

        if (_currentTab == DetailTabKind.Files)
            EnsureTabLoaded(DetailTabKind.Files, force: true);
    }

    private static void DisableWithReason(FrameworkElement element, string why)
    {
        element.IsEnabled = false;
        ToolTipService.SetShowOnDisabled(element, true);
        element.ToolTip = why;
    }

    private async Task TrySyncSharedContentAsync()
    {
        if (_pack is null || !_pack.IsShared || _isOwner) return;
        if (!_pack.EffectivePermissions.HasFlag(PackPermissions.Download)) return;

        try
        {
            var manifest = await App.State.Api.GetManifestAsync(_pack.Id);
            if (manifest.Entries.Count == 0) return;

            var locallySynced = App.State.Settings.PackSyncedVersion.TryGetValue(_pack.Id, out var v) ? v : 0;
            if (manifest.Version <= locallySynced) return;

            StatusLabel.Text = "Syncing shared files...";
            if (!await RunSharedDownloadAsync(null)) return;
            ApplyHeroIcon(_pack);
            ApplyDescriptionDisplay(_pack.Id, _pack.Description ?? "");
            UpdateStatsLabel(_pack.Id);
            if (_tabsLoaded.Contains(DetailTabKind.Mods))
                ModListCtrl.Load(_pack, ListOwnerWindow);
            if (_tabsLoaded.Contains(DetailTabKind.Files))
                await RefreshFileListsAsync(runAutoApply: false);
            StatusLabel.Text = "Synced from server.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Sync failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Links a pack with team associations into each "team:&lt;id&gt;" folder, so team-shared packs
    /// show up in the team folder.
    /// </summary>
    private void AutoLinkToTeamFolders(PackDetail pack)
    {
        if (pack.Teams.Count == 0) return;
        var changed = false;
        foreach (var teamEntry in pack.Teams)
        {
            var key = $"team:{teamEntry.TeamId:N}";
            if (!App.State.Settings.PackFolders.TryGetValue(key, out var list))
            {
                list = new();
                App.State.Settings.PackFolders[key] = list;
                changed = true;
            }
            if (!list.Contains(pack.Id)) { list.Add(pack.Id); changed = true; }
        }
        if (changed) App.State.Settings.Save();
    }

    // ── Minecraft logs (game/logs/) ──────────────────────────────────────────

    /// <summary>
    /// Rebuilds the log list: the two live pseudo-logs, then every file in game/logs and every
    /// crash report in game/crash-reports, newest first.
    /// </summary>
    /// <remarks>Crash reports are listed too, since <c>crash-reports/crash-*.txt</c> is what explains a
    /// crash.</remarks>
    private void RefreshLogsList()
    {
        if (_pack is null) return;
        var rows = new List<LogRow>
        {
            LogRow.LauncherLogPseudo(),
            LogRow.SyncLogPseudo()
        };

        var gameDir = App.State.Packs.GameDir(_packId);
        var files = new List<LogRow>();
        CollectLogFiles(Path.Combine(gameDir, "logs"), "*.log*", isCrashReport: false, files);
        CollectLogFiles(Path.Combine(gameDir, "crash-reports"), "crash-*.txt", isCrashReport: true, files);
        rows.AddRange(files.OrderByDescending(r => r.Kind == LogRowKind.CrashReport)
                           .ThenByDescending(r => r.SortTime));

        var previouslySelected = (LogsList.SelectedItem as LogRow)?.Key;
        LogsList.ItemsSource = rows;

        var logCount = files.Count(r => r.Kind == LogRowKind.GameLog);
        var crashCount = files.Count - logCount;
        LogsCountLabel.Text = (logCount, crashCount) switch
        {
            (0, 0) => "No Minecraft logs yet",
            (_, 0) => $"{logCount} log{(logCount == 1 ? "" : "s")}",
            (0, _) => $"{crashCount} crash report{(crashCount == 1 ? "" : "s")}",
            _ => $"{logCount} log{(logCount == 1 ? "" : "s")} · {crashCount} crash report{(crashCount == 1 ? "" : "s")}"
        };

        var match = previouslySelected != null
            ? rows.FirstOrDefault(r => r.Key == previouslySelected)
            : null;
        LogsList.SelectedItem = match ?? rows[0];
    }

    private void CollectLogFiles(string dir, string pattern, bool isCrashReport, List<LogRow> into)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles(pattern))
                into.Add(new LogRow(f, isCrashReport));
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Could not read {Path.GetFileName(dir)}: {ex.Message}";
        }
    }

    private void OnRefreshLogs(object sender, RoutedEventArgs e) => RefreshLogsList();

    // ── Logs tab: find, level filter, copy, save ─────────────────────────────

    /// <summary>The find box's comfortable width. The header only uses one row when the box can get
    /// this much; narrower still works but truncates the placeholder.</summary>
    private const double LogFindComfortPx = 220;

    /// <summary>What the "N of M lines" caption is allowed on one row. It trims past this, so a long
    /// count never decides the layout.</summary>
    private const double LogLinesBudgetPx = 110;

    private void OnLogHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) LayoutLogHeader();
    }

    /// <summary>
    /// Puts find and level beside the title when everything fits on one row, otherwise on their own row
    /// under the title.
    /// </summary>
    /// <remarks>
    /// Measured rather than a fixed breakpoint, because button widths depend on the font and UI scale.
    /// Uses the DesiredSize from the last layout pass: re-measuring a docked child at infinity by hand
    /// would stick, and it would then be arranged by that size.
    /// </remarks>
    private void LayoutLogHeader()
    {
        // The labelled width can only be measured while the labels show; when folded to icons, use the
        // last labelled measurement.
        if (CopyLogLabel.Visibility == Visibility.Visible)
            _logActionsLabelledPx = LogActions.Children.OfType<UIElement>().Sum(c => c.DesiredSize.Width);

        var title = LogTitleBlock.DesiredSize.Width;
        var width = LogHeaderGrid.ActualWidth;
        var filters = LogFindComfortPx + LogLevelBox.Width + LogLevelBox.Margin.Left
                      + LogLinesLabel.Margin.Left + LogLinesBudgetPx + LogFilterWideSlot.Margin.Right;
        var wide = width >= title + filters + _logActionsLabelledPx;

        // If the labelled buttons still do not fit beside the title, fold them to icons (they have
        // tooltips) instead of wrapping one onto its own line.
        SetLogActionLabels(wide || width >= title + _logActionsLabelledPx);

        var target = wide ? LogFilterWideSlot : LogFilterNarrowSlot;
        LogFilterNarrowSlot.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        if (ReferenceEquals(LogFilterRow.Parent, target)) return;
        if (LogFilterRow.Parent is Border from) from.Child = null;
        target.Child = LogFilterRow;
    }

    /// <summary>The actions' one-row width with their labels, from the last pass that
    /// showed them.</summary>
    private double _logActionsLabelledPx;

    private void SetLogActionLabels(bool shown)
    {
        foreach (var label in new[] { CopyLogLabel, SaveLogLabel, OpenLogsFolderLabel })
        {
            label.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            // The glyph's gap belongs to the label; an icon-only button is centred without it.
            if (label.Parent is Panel { Children.Count: > 1 } row && row.Children[0] is FrameworkElement glyph)
                glyph.Margin = shown ? new Thickness(0, 0, 6, 0) : new Thickness(0);
        }
    }

    private void OnLogFilterChanged(object sender, TextChangedEventArgs e)
    {
        LogContent.FilterText = LogFilterBox.Text.Trim();
    }

    private void OnLogFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        LogFilterBox.Clear();
        e.Handled = true;
    }

    private void OnLogLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogContent is null) return; // fires once while the template is still loading
        LogContent.FilterMode = LogLevelBox.SelectedIndex switch
        {
            1 => LogFilterMode.Problems,
            2 => LogFilterMode.Errors,
            _ => LogFilterMode.All
        };
    }

    /// <summary>Updates the "N of M lines" caption and notes when the buffer cap has dropped the start
    /// of the log, so missing early lines are not mistaken for a mod that never loaded.</summary>
    private void UpdateLogLinesLabel()
    {
        if (LogLinesLabel is null) return;

        var shown = LogContent.VisibleLineCount;
        var total = LogContent.TotalLineCount;
        var text = LogContent.IsFiltered
            ? $"{shown:N0} of {total:N0} lines"
            : $"{total:N0} lines";
        if (LogContent.TrimmedLineCount > 0)
            text += $" · earliest {LogContent.TrimmedLineCount:N0} trimmed";
        LogLinesLabel.Text = text;
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        var text = LogContent.IsFiltered ? LogContent.VisibleText : LogContent.Text;
        if (string.IsNullOrWhiteSpace(text)) { StatusLabel.Text = "There is nothing in this log to copy."; return; }
        StatusLabel.Text = ClipboardHelper.TrySetText(text)
            ? LogContent.IsFiltered
                ? $"Copied {LogContent.VisibleLineCount:N0} matching line(s)."
                : "Log copied to the clipboard."
            : "Could not reach the clipboard - another program is holding it.";
    }

    private async void OnSaveLogAs(object sender, RoutedEventArgs e)
    {
        try
        {
            if (LogsList.SelectedItem is not LogRow row) return;
            var text = LogContent.IsFiltered ? LogContent.VisibleText : LogContent.Text;
            if (string.IsNullOrWhiteSpace(text)) { StatusLabel.Text = "There is nothing in this log to save."; return; }

            var suggested = row.HasFile
                ? Path.GetFileNameWithoutExtension(row.Path) + ".txt"
                : $"{SafeFileName(_pack?.Name ?? "instance")}-{row.DisplayName.ToLowerInvariant().Replace(' ', '-')}.txt";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save log",
                Filter = "Text file (*.txt)|*.txt|Log file (*.log)|*.log|All files (*.*)|*.*",
                FileName = suggested,
                AddExtension = true,
                DefaultExt = ".txt"
            };
            if (dlg.ShowDialog(ListOwnerWindow) != true) return;

            var path = dlg.FileName;
            await Task.Run(() => File.WriteAllText(path, text));
            StatusLabel.Text = "Saved " + path;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not save the log: " + ex.Message;
        }
    }

    /// <summary>Strips the characters Windows will not take in a file name.</summary>
    private static string SafeFileName(string value)
    {
        var cleaned = new string(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "instance" : cleaned.Trim();
    }

    // ── Logs tab: right-click menu ───────────────────────────────────────────

    private LogRow? SelectedLogRow => LogsList.SelectedItem as LogRow;

    private void OnLogOpenInEditor(object sender, RoutedEventArgs e)
    {
        if (RequireLogFile() is not { } row) return;
        try
        {
            FileEditorWindow.OpenFileFor(Window.GetWindow(this), _packId, _pack?.Name ?? "Instance", row.Path);
        }
        catch (Exception ex) { StatusLabel.Text = "Could not open that log: " + ex.Message; }
    }

    private void OnLogCopyContents(object sender, RoutedEventArgs e) => OnCopyLog(sender, e);

    private void OnLogCopyPath(object sender, RoutedEventArgs e)
    {
        if (RequireLogFile() is not { } row) return;
        StatusLabel.Text = ClipboardHelper.TrySetText(row.Path)
            ? "Path copied."
            : "Could not reach the clipboard.";
    }

    private void OnLogReveal(object sender, RoutedEventArgs e)
    {
        if (RequireLogFile() is not { } row) return;
        RevealInExplorer(row.Path);
    }

    private async void OnLogDelete(object sender, RoutedEventArgs e)
    {
        try
        {
            if (RequireLogFile() is not { } row) return;
            var ok = await AppDialog.ConfirmAsync(ListOwnerWindow, "Delete log",
                $"Delete {row.DisplayName}? This removes the file from disk.",
                "Delete", "Cancel", danger: true);
            if (!ok) return;

            File.Delete(row.Path);
            StatusLabel.Text = $"Deleted {row.DisplayName}.";
            RefreshLogsList();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not delete that log: " + ex.Message;
        }
    }

    /// <summary>The selected row if it is a real file, otherwise null with the reason on screen. The
    /// launcher and sync rows are live buffers, so reveal and delete do not apply.</summary>
    private LogRow? RequireLogFile()
    {
        var row = SelectedLogRow;
        if (row is null) { StatusLabel.Text = "Select a log first."; return null; }
        if (!row.HasFile)
        {
            StatusLabel.Text = $"'{row.DisplayName}' is a live view, not a file on disk - use Copy or Save as... instead.";
            return null;
        }
        return row;
    }

    /// <summary>Opens Explorer with the file selected, rather than just opening its folder.</summary>
    private void RevealInExplorer(string path)
    {
        if (!SafeLaunch.RevealFile(path)) StatusLabel.Text = "Could not open Explorer.";
    }

    /// <summary>Opens the instance's game folder (mods/, config/, saves/...) in Explorer.</summary>
    private void OnOpenPackFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = App.State.Packs.GameDir(_packId);
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) StatusLabel.Text = "Could not open the folder.";
        }
        catch (Exception ex) { StatusLabel.Text = "Could not open the folder: " + ex.Message; }
    }

    /// <summary>Exports this instance as a CurseForge .zip or a Modrinth .mrpack through
    /// <see cref="ExportPackDialog"/>.</summary>
    /// <remarks>Needs the instance record for the Minecraft version and loader. Hidden in the game
    /// overlay, where the card would open behind the game.</remarks>
    private async void OnExportPack(object sender, RoutedEventArgs e)
    {
        if (_pack is null)
        {
            StatusLabel.Text = "This instance is still loading. Try again in a moment.";
            return;
        }
        try
        {
            await ExportPackDialog.ShowAsync(_shell, _pack);
        }
        catch (Exception ex)
        {
            AppLog.LogError("packs.export", ex);
            StatusLabel.Text = "The export could not be opened. The details are in the launcher log.";
        }
    }

    /// <summary>
    /// "Edit files" in the header and "File Management" on the Files tab both open the
    /// file-management page.
    /// </summary>
    /// <remarks>
    /// Falls back to the standalone editor window in the Minecraft host overlay, which has no side
    /// panel, and when there is no instance record, since the editor only needs the id.
    /// </remarks>
    private void OnEditPackFiles(object sender, RoutedEventArgs e) => OpenFileManagement();

    private void OnOpenFileManagement(object sender, RoutedEventArgs e) => OpenFileManagement();

    private void OpenFileManagement()
    {
        try
        {
            if (_hostedInMinecraftWindow || _pack is null)
            {
                FileEditorWindow.Open(Window.GetWindow(this), _packId, _pack?.Name ?? "Instance");
                return;
            }
            _shell.OpenFileManagementForPack(_pack);
        }
        catch (Exception ex)
        {
            AppLog.LogError("packs.file-management", ex);
            StatusLabel.Text = "Could not open file management. The details are in the launcher log.";
        }
    }

    /// <summary>
    /// The Overview tab's primary button: opens this instance's Modpack Management hub.
    /// </summary>
    /// <remarks>
    /// In the Minecraft host window the hub would open on the launcher window behind the game, so the
    /// lightweight mod explorer is pushed into the overlay's own strip instead
    /// (<see cref="ApplyHostedTabChrome"/> relabels the button to match).
    /// </remarks>
    private void OnOpenModHub(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        try
        {
            if (_hostedInMinecraftWindow)
            {
                // Push into the host window's strip. ModExplorerWindow is the fallback when the
                // page has already been detached from its host and GetWindow returns nothing.
                if (Window.GetWindow(this) is MinecraftHostWindow host)
                    host.OpenModExplorerForPack(_pack);
                else
                    new ModExplorerWindow(_pack) { Owner = Window.GetWindow(this) }.Show();
                return;
            }
            _shell.OpenModManagementForPack(_pack);
        }
        catch (Exception ex)
        {
            AppLog.LogError("packs.mod-hub", ex);
            StatusLabel.Text = "Modpack management could not be opened. The details are in the launcher log.";
        }
    }

    /// <summary>Files tab: "Edit" on the selected file, or double-clicking one.</summary>
    private void OnEditSelectedGameFile(object sender, RoutedEventArgs e)
    {
        var selected = GameView.GetSelectedFiles().FirstOrDefault();
        if (selected is null) { StatusLabel.Text = "Select a file to edit."; return; }
        OpenInEditor(selected);
    }

    private void OpenInEditor(string relativePath)
    {
        try
        {
            var full = Path.Combine(App.State.Packs.GameDir(_packId),
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) { StatusLabel.Text = "That file is no longer there."; return; }
            if (TextFileService.IsKnownBinary(full))
            {
                // The editor cannot help with jars or images. Images open in their viewer; anything
                // else is shown in Explorer rather than run.
                if (!SafeLaunch.OpenFile(full)) StatusLabel.Text = "Could not open that file.";
                return;
            }
            FileEditorWindow.OpenFileFor(Window.GetWindow(this), _packId, _pack?.Name ?? "Instance", full);
        }
        catch (Exception ex) { StatusLabel.Text = "Could not open that file: " + ex.Message; }
    }

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(App.State.Packs.GameDir(_packId), "logs");
        Directory.CreateDirectory(dir);
        if (!SafeLaunch.OpenFolder(dir)) StatusLabel.Text = "Could not open the folder.";
    }

    /// <summary>Selects the row under the cursor on right-click, which WPF does not do, so the
    /// context menu acts on that log.</summary>
    private void OnLogsListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        if (ItemsControl.ContainerFromElement(LogsList, src) is ListBoxItem item)
            item.IsSelected = true;
    }

    private async void OnLogsSelected(object sender, SelectionChangedEventArgs e)
    {
        // Unsubscribe launcher-log tailing whenever the selection changes
        AppLog.MessageAppended -= OnLauncherLogAppended;

        if (LogsList.SelectedItem is not LogRow row) { LogContent.Clear(); return; }
        if (row.IsLauncherLog)
        {
            LogContent.SetText(AppLog.Buffer);
            AppLog.MessageAppended += OnLauncherLogAppended;
            return;
        }
        if (row.IsSyncLog) { LogContent.SetText(LogBox.Text); return; }
        try
        {
            // A modded latest.log can be tens of megabytes, so read it off the UI thread.
            var path = row.Path;
            var text = await Task.Run(() => ReadLogText(path));
            // The user may have clicked another log while this one was being read.
            if ((LogsList.SelectedItem as LogRow)?.Key != row.Key) return;
            LogContent.SetText(text);
        }
        catch (Exception ex) { LogContent.SetText("Could not read log: " + ex.Message); }
    }

    /// <summary>Reads a log file, un-gzipping a rolled one, and keeps only the tail of a very long
    /// file, since the end is where the failure is.</summary>
    private static string ReadLogText(string path)
    {
        string text;
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(path);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            text = sr.ReadToEnd();
        }
        else
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            text = sr.ReadToEnd();
        }

        const int maxChars = 200_000;
        if (text.Length > maxChars) text = "[...older lines trimmed...]\n" + text[^maxChars..];
        return text;
    }

    /// <summary>Mirrors the hidden LogBox into the Logs tab while the sync log is shown.</summary>
    private void OnLogBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (LogsList.SelectedItem is LogRow row && row.IsSyncLog)
            LogContent.SetText(LogBox.Text);
    }

    /// <summary>Appends a launcher-log line to the content view while that entry is selected.</summary>
    private void OnLauncherLogAppended(string line)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnLauncherLogAppended(line)); return; }
        LogContent.Append(line);
        LogContent.ScrollToEnd();
    }

    /// <summary>Max chars kept in the in-memory log TextBoxes. TextBox append cost grows with its
    /// content, so capping it stops lag building up over a long session. Matches the on-disk
    /// log-viewer cap.</summary>
    private const int MaxLogChars = 200_000;

    /// <summary>Append text to a log TextBox, trimming the oldest content once it exceeds the cap.
    /// Drops a chunk at a time so we don't re-trim on every single append.</summary>
    private static void AppendCapped(TextBox box, string text)
    {
        box.AppendText(text);
        if (box.Text.Length <= MaxLogChars) return;
        var kept = box.Text[^(MaxLogChars * 9 / 10)..];
        box.Text = "[...older lines trimmed...]" + Environment.NewLine + kept;
        box.CaretIndex = box.Text.Length;
    }

    private async void RefreshWorlds()
    {
        if (_pack is null) return;
        _worldRows.Clear();
        WorldCountLabel.Text = "Scanning saves...";
        try
        {
            var packId = _pack.Id;
            var packName = _pack.Name;
            var all = await App.State.Api.ListPacksAsync();
            var worlds = await Task.Run(() => App.State.Worlds.ScanPack(packId, packName));
            foreach (var w in worlds) _worldRows.Add(new WorldRow(w, all));
            WorldCountLabel.Text = $"{_worldRows.Count} save{(_worldRows.Count == 1 ? "" : "s")}";
            WorldsEmptyLabel.Visibility = _worldRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { /* offline or no api, skip */ }
    }

    /// <summary>
    /// Rebuilds the Screenshots tab: the folder scan runs off the UI thread, the cards appear
    /// immediately, and the pictures are decoded afterwards in small batches.
    /// </summary>
    /// <remarks>Thumbnails are decoded at card size, since full-size 4K captures would hold gigabytes
    /// of bitmaps.</remarks>
    private async void RefreshPackScreenshots()
    {
        _screenshotCts?.Cancel();
        var cts = new CancellationTokenSource();
        _screenshotCts = cts;
        var ct = cts.Token;

        _packScreenshotRows.Clear();
        PackScreenshotCountLabel.Text = "Looking for screenshots...";

        try
        {
            var folders = PackScreenshotFolders().ToList();
            var canSetAsIcon = _isOwner;
            var rows = await Task.Run(() =>
            {
                var found = new List<PackScreenshotRow>();
                foreach (var (dir, label) in folders)
                {
                    if (!Directory.Exists(dir)) continue;
                    ct.ThrowIfCancellationRequested();
                    foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                    {
                        if (IsScreenshotFile(file))
                            found.Add(new PackScreenshotRow(file, label, canSetAsIcon));
                    }
                }
                return found.OrderByDescending(r => r.LastWriteTime).ToList();
            }, ct);

            if (ct.IsCancellationRequested) return;

            foreach (var row in rows)
                _packScreenshotRows.Add(row);

            PackScreenshotList.Visibility = _packScreenshotRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PackScreenshotsEmptyText.Visibility = _packScreenshotRows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            PackScreenshotCountLabel.Text = _packScreenshotRows.Count switch
            {
                0 => "No screenshots found in this instance.",
                1 => "1 screenshot found in this instance.",
                var count => $"{count} screenshots found in this instance."
            };

            await LoadScreenshotThumbnailsAsync(rows, ct);
        }
        catch (OperationCanceledException) { /* a newer refresh, or the page closed */ }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not read screenshots: " + ex.Message;
            PackScreenshotCountLabel.Text = "Could not read the screenshots folder.";
        }
    }

    /// <summary>Decodes card-sized thumbnails a batch at a time, handing each batch back to the UI
    /// before starting the next so the tab stays usable while a big folder fills in.</summary>
    private static async Task LoadScreenshotThumbnailsAsync(IReadOnlyList<PackScreenshotRow> rows, CancellationToken ct)
    {
        const int batchSize = 8;
        for (var i = 0; i < rows.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = rows.Skip(i).Take(batchSize).ToList();
            var decoded = await Task.Run(
                () => batch.Select(r => (Row: r, Image: PackScreenshotRow.DecodeThumbnail(r.Source.FullName))).ToList(),
                ct);
            if (ct.IsCancellationRequested) return;
            foreach (var (row, image) in decoded)
                row.SetThumbnail(image);
        }
    }

    private IEnumerable<(string Path, string Label)> PackScreenshotFolders()
    {
        if (_pack is null) yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in CandidatePackScreenshotFolders())
        {
            if (seen.Add(folder.Path))
                yield return folder;
        }
    }

    private IEnumerable<(string Path, string Label)> CandidatePackScreenshotFolders()
    {
        yield return (Path.Combine(App.State.Packs.GameDir(_packId), "screenshots"), "Game");
    }

    private static bool IsScreenshotFile(FileInfo file) =>
        ScreenshotExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase);

    private void OnRefreshPackScreenshots(object sender, RoutedEventArgs e) => RefreshPackScreenshots();

    private void OnOpenPackScreenshotsFolder(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(App.State.Packs.GameDir(_packId), "screenshots");
        OpenInExplorer(dir);
    }

    private void OnOpenPackScreenshot(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PackScreenshotRow row }) return;
        e.Handled = true;
        OpenScreenshotPreview(row);
    }

    // ── Screenshots tab: right-click menu ────────────────────────────────────

    /// <summary>Opens the preview window on <paramref name="row"/>, handing it the whole tab so the
    /// arrow keys can walk the rest of the folder without coming back here.</summary>
    private void OpenScreenshotPreview(PackScreenshotRow row)
    {
        var siblings = _packScreenshotRows
            .Select(r => (r.FullImageUrl, r.Title))
            .ToList();
        var index = _packScreenshotRows.IndexOf(row);
        ScreenshotPreviewWindow.ShowFor(Window.GetWindow(this), row.FullImageUrl, row.Title, siblings, index);
    }

    /// <summary>The row a screenshot context-menu item was raised for. Cards carry their own
    /// DataContext, so nothing needs to be selected first.</summary>
    private static PackScreenshotRow? ScreenshotRowOf(object sender)
    {
        if (sender is not FrameworkElement element) return null;
        if (element.DataContext is PackScreenshotRow direct) return direct;
        // A context menu is not in the card's visual tree, so if the inherited DataContext has not
        // arrived, ask the card the menu was opened on.
        return (element.Parent as ContextMenu)?.PlacementTarget is FrameworkElement target
            ? target.DataContext as PackScreenshotRow
            : null;
    }

    private void OnScreenshotOpen(object sender, RoutedEventArgs e)
    {
        if (ScreenshotRowOf(sender) is { } row) OpenScreenshotPreview(row);
    }

    private void OnScreenshotCopyImage(object sender, RoutedEventArgs e)
    {
        if (ScreenshotRowOf(sender) is not { } row) return;
        try
        {
            // Copy the image itself so it can be pasted into a chat or an issue, decoded at full
            // size rather than reusing the card's thumbnail.
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(row.Source.FullName);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            Clipboard.SetImage(image);
            StatusLabel.Text = $"Copied {row.Source.Name} to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not copy that image: " + ex.Message;
        }
    }

    private void OnScreenshotCopyPath(object sender, RoutedEventArgs e)
    {
        if (ScreenshotRowOf(sender) is not { } row) return;
        StatusLabel.Text = ClipboardHelper.TrySetText(row.Source.FullName)
            ? "Path copied."
            : "Could not reach the clipboard.";
    }

    private void OnScreenshotReveal(object sender, RoutedEventArgs e)
    {
        if (ScreenshotRowOf(sender) is { } row) RevealInExplorer(row.Source.FullName);
    }

    private void OnScreenshotSetAsIcon(object sender, RoutedEventArgs e)
    {
        if (ScreenshotRowOf(sender) is not { } row) return;
        if (_pack is null || !_isOwner) { StatusLabel.Text = "Only the instance owner can change its image."; return; }
        try
        {
            App.State.PackAssets.SaveIconFromFile(_pack.Id, row.Source.FullName);
            ApplyHeroIcon(_pack);
            UpdateImageEditUi();
            _shell.RefreshPackCover(_pack.Id);
            StatusLabel.Text = $"'{row.Title}' is now this instance's image.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Couldn't set image: " + ex.Message;
        }
    }

    private void OnScreenshotRename(object sender, RoutedEventArgs e)
    {
        if (ScreenshotRowOf(sender) is not { } row) return;
        try
        {
            var extension = row.Source.Extension;
            var dlg = new SimpleInputDialog("Rename screenshot", "New name", row.Title) { Owner = ListOwnerWindow };
            if (dlg.ShowDialog() != true) return;

            var name = (dlg.Result ?? "").Trim();
            if (name.Length == 0 || string.Equals(name, row.Title, StringComparison.Ordinal)) return;
            if (name.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            {
                StatusLabel.Text = "That name has characters Windows will not allow in a file name.";
                return;
            }

            // Keep the extension: Windows and the tab's own scanner both go by it.
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name += extension;
            if (!PathSafety.IsSafeFileName(name))
            {
                StatusLabel.Text = "Windows will not allow that as a file name.";
                return;
            }
            var target = Path.Combine(row.Source.DirectoryName ?? "", name);
            if (File.Exists(target)) { StatusLabel.Text = "There is already a screenshot with that name."; return; }

            File.Move(row.Source.FullName, target);
            StatusLabel.Text = $"Renamed to {name}.";
            RefreshPackScreenshots();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not rename that screenshot: " + ex.Message;
        }
    }

    private async void OnScreenshotDelete(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ScreenshotRowOf(sender) is not { } row) return;
            var ok = await AppDialog.ConfirmAsync(ListOwnerWindow, "Delete screenshot",
                $"Delete {row.Source.Name}? It is removed from disk and cannot be recovered from the launcher.",
                "Delete", "Cancel", danger: true);
            if (!ok) return;

            File.Delete(row.Source.FullName);
            StatusLabel.Text = $"Deleted {row.Source.Name}.";
            RefreshPackScreenshots();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not delete that screenshot: " + ex.Message;
        }
    }

    private void OnWorldOpenFolder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string path && !SafeLaunch.OpenFolder(path))
            StatusLabel.Text = "Could not open that folder.";
    }

    private void OnWorldEditCompatibility(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string key && el.DataContext is WorldRow row)
            _shell.OpenWorldDetail(key, row.DisplayName);
    }

    private void ApplyPack(PackDetail pack)
    {
        _suppressEvents = true;
        try
        {
            PackNameLabel.Text = pack.Name;
            PackVersionLabel.Text = pack.IsEmpty
                ? "Empty instance (no Minecraft launch)"
                : pack.Loader == LoaderKind.None
                    ? $"Minecraft {pack.MinecraftVersion}"
                    : $"Minecraft {pack.MinecraftVersion} · {pack.Loader} {pack.LoaderVersion}";
            var summary = pack.Summary ?? "";
            var description = pack.Description ?? "";
            PackMetaLabel.Text = $"by {pack.OwnerUsername} · updated {pack.UpdatedAt.LocalDateTime:g}";
            SetPackSummary(summary);
            ApplyHeroIcon(pack);
            ApplyDescriptionDisplay(pack.Id, description);
            UpdateStatsLabel(pack.Id);
            _lastSavedSummary = summary;
            _lastSavedDescription = description;
            OverviewSummaryBox.Text = summary;
            OverviewSummaryBox.IsReadOnly = !_isOwner;
            OverviewSummaryBox.ToolTip = _isOwner
                ? "Summary changes auto-save to the server."
                : "Only the instance owner can edit this summary.";
            OverviewSaveStatusLabel.Text = _isOwner ? "Auto-saves to server" : "Read-only";
            // Importing replaces the editable description, so it's owner-only and not offered
            // in the read-only hosted-Minecraft view. Export is always available.
            ImportDescriptionButton.Visibility = _isOwner && !_hostedInMinecraftWindow
                ? Visibility.Visible
                : Visibility.Collapsed;
            RenameInstanceButton.Visibility = _isOwner
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateImageEditUi();
            UpdateOverviewCharacterCount();

            PackRootHint.Text = "Instance root: " + App.State.Packs.PackRoot(pack.Id);

            HeroTags.ItemsSource = BuildTags(pack);

            VisibilityBox.SelectedIndex = (int)pack.Visibility;
            IsSharedBox.IsChecked = pack.IsShared;

            ApplyLoaderCard(pack);

            // Always shown: most of this tab is the local instance folder, which every collaborator and
            // purely local instance has, and which works with the server down.
            FilesTab.Visibility   = Visibility.Visible;
            SharingOptionsPanel.Visibility = _isOwner ? Visibility.Visible : Visibility.Collapsed;
            ApplyHostedTabChrome();

            // RAM + auto-update
            var ram = App.State.Settings.GetMaxRamFor(pack.Id);
            RamSlider.Value = ram;
            RamValueLabel.Text = $"{ram} MB";
            JvmArgsBox.Text = App.State.Settings.GetJvmArgsFor(pack.Id);
            _ = LoadJavaChoicesAsync(pack);
            AutoUpdateBox.IsChecked = App.State.Settings.GetAutoUpdateFor(pack.Id, pack.IsShared, pack.OwnerId);
            // Auto-update is only for copies you consume: for the owner or uploaders, pulling the
            // server copy before each launch would overwrite local work.
            var canEditPack = pack.OwnerId == App.State.Settings.UserId
                              || pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared);
            AutoUpdateCard.Visibility = canEditPack ? Visibility.Collapsed : Visibility.Visible;
            PackPageCollapsedBox.SelectedIndex =
                App.State.Settings.GetMinecraftWindowPackPageCollapsedOverride(pack.Id) switch
                {
                    null => 0,
                    false => 1,
                    true => 2
                };
            BorderlessFullscreenBox.SelectedIndex =
                App.State.Settings.GetMinecraftWindowBorderlessFullscreenOverride(pack.Id) switch
                {
                    null => 0,
                    false => 1,
                    true => 2
                };

            // Folder buttons. "Open game folder" is always available, since the folder is local.
            var hasShared = pack.IsShared;
            OpenLocalButton.IsEnabled = true;
            OpenSharedButton.IsEnabled = hasShared;
            UploadButton.IsEnabled = hasShared && pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared);
            UploadButton.ToolTip = UploadButton.IsEnabled
                ? "Upload the files marked shared to CloudLauncher"
                : hasShared
                    ? "You do not have permission to upload files to this instance."
                    : "Turn on Cloud sync in Options before uploading anything.";
            ToolTipService.SetShowOnDisabled(UploadButton, true);
            OpenFileManagementButton.IsEnabled = true;
            OpenFileManagementButton.ToolTip =
                "Browse, edit, share, host, clean up and compare this instance's files";
            ApplySharingPermissionsToFilesTab();

            // Launch buttons
            var playable = !pack.IsEmpty && !string.IsNullOrEmpty(pack.MinecraftVersion);
            UpdateLaunchButtonState();

            // Both export formats need the Minecraft version; without one there is nothing to export.
            ExportPackButton.IsEnabled = playable;
            ExportPackButton.ToolTip = playable
                ? "Save this instance as a CurseForge .zip or Modrinth .mrpack that other launchers can install"
                : "This instance has no Minecraft version yet, so there is nothing to export.";
            UpdateServerChip();
            OpenServerButton.IsEnabled = playable;

            // Update button is hidden until CheckForUpdateAsync confirms one is available
            UpdateButton.Visibility = Visibility.Collapsed;

            // Auto-apply rules: on by default, persisted per pack. Only relevant when the pack is
            // shared (so local and shared destinations exist) and the user can edit.
            AutoApplyRow.Visibility = hasShared && _isOwner ? Visibility.Visible : Visibility.Collapsed;
            AutoApplyBox.IsEnabled = pack.IsShared;
            AutoApplyBox.IsChecked = pack.IsShared && App.State.Settings.GetAutoApplyRulesFor(pack.Id);

            UpdateAutoApplyUi();
        }
        finally { _suppressEvents = false; }
    }

    private async void OnRenameInstance(object sender, RoutedEventArgs e)
    {
        if (_pack is null || !_isOwner) return;

        var dlg = new SimpleInputDialog("Rename instance", "New name", _pack.Name) { Owner = ListOwnerWindow };
        if (dlg.ShowDialog() != true) return;

        var name = (dlg.Result ?? "").Trim();
        if (string.IsNullOrEmpty(name) || string.Equals(name, _pack.Name, StringComparison.Ordinal))
            return;

        StatusLabel.Text = "Renaming instance...";
        try
        {
            var updated = await App.State.Api.UpdatePackAsync(
                _pack.Id,
                new UpdatePackRequest(name, null, null, null, null, null, null, null));
            App.State.Packs.TryRenameFolder(_pack.Id, name); // keep the on-disk folder in step
            _shell.AddOrUpdatePackList(updated);
            await ReloadAsync();
            StatusLabel.Text = "Instance renamed.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Rename failed: " + ex.Message;
        }
    }

    /// <summary>Shows cover editing to owners, and "Remove image" only when there is one.</summary>
    private void UpdateImageEditUi()
    {
        HeroIconEditButton.Visibility = _isOwner ? Visibility.Visible : Visibility.Collapsed;
        RemoveImageMenuItem.IsEnabled = _pack is not null && App.State.PackAssets.HasIcon(_pack.Id);
    }

    private void OnChangeImage(object sender, RoutedEventArgs e)
    {
        if (_pack is null || !_isOwner) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose instance image",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"
                     + "|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(ListOwnerWindow) != true) return;

        try
        {
            App.State.PackAssets.SaveIconFromFile(_pack.Id, dlg.FileName);
            ApplyHeroIcon(_pack);
            UpdateImageEditUi();
            _shell.RefreshPackCover(_pack.Id);
            StatusLabel.Text = "Image updated.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Couldn't set image: " + ex.Message;
        }
    }

    private void OnRemoveImage(object sender, RoutedEventArgs e)
    {
        if (_pack is null || !_isOwner || !App.State.PackAssets.HasIcon(_pack.Id)) return;

        App.State.PackAssets.RemoveIcon(_pack.Id);
        ApplyHeroIcon(_pack);
        UpdateImageEditUi();
        _shell.RefreshPackCover(_pack.Id);
        StatusLabel.Text = "Image removed.";
    }

    /// <summary>Re-reads the cover from disk after it changed elsewhere (e.g. the pack list).</summary>
    public void RefreshHeroIcon()
    {
        if (_pack is null) return;
        ApplyHeroIcon(_pack);
        UpdateImageEditUi();
    }

    private void OnOverviewSummaryChanged(object sender, TextChangedEventArgs e)
    {
        UpdateOverviewCharacterCount();
        SetPackSummary(OverviewSummaryBox.Text);
        QueueOverviewSaveIfChanged();
    }

    private async void OnOverviewSummaryLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _pack is null || !_isOwner) return;
        _overviewSaveTimer.Stop();
        await SaveOverviewDescriptionAsync();
    }

    private void OnDescriptionEditorChanged()
    {
        if (_suppressEvents || !_descriptionEditing) return;
        RefreshCurrentDescriptionHtml();
        UpdateOverviewCharacterCount();
        QueueOverviewSaveIfChanged();
    }

    private void RefreshCurrentDescriptionHtml()
    {
        if (OverviewDescriptionEditor.TryGetHtml(out var html))
            _currentDescriptionHtml = html;
    }

    private string GetSavedDescriptionSource() => _lastSavedDescriptionHtml;

    private void QueueOverviewSaveIfChanged()
    {
        if (_suppressEvents || _pack is null || !_isOwner) return;
        _overviewSaveTimer.Stop();
        if (OverviewSummaryBox.Text == _lastSavedSummary
            && (!_customFormattedDescription || _currentDescriptionHtml == _lastSavedDescriptionHtml))
        {
            OverviewSaveStatusLabel.Text = "Saved";
            return;
        }

        OverviewSaveStatusLabel.Text = "Unsaved changes";
        _overviewSaveTimer.Start();
    }

    private async void OnOverviewSaveTimerTick(object? sender, EventArgs e)
    {
        _overviewSaveTimer.Stop();
        await SaveOverviewDescriptionAsync();
    }

    private async Task SaveOverviewDescriptionAsync()
    {
        if (_pack is null || !_isOwner) return;

        var summary = OverviewSummaryBox.Text;
        var description = GetDescriptionExcerptForServer();
        if (_customFormattedDescription && _descriptionEditing)
            App.State.PackAssets.SaveCustomDescriptionHtml(_pack.Id, _currentDescriptionHtml);
        else if (!string.IsNullOrWhiteSpace(description))
            App.State.PackAssets.MirrorToSharedFolder(_pack.Id);
        if (summary == _lastSavedSummary && description == _lastSavedDescription)
        {
            OverviewSaveStatusLabel.Text = "Saved";
            return;
        }

        if (_isSavingOverview)
        {
            _overviewSavePending = true;
            return;
        }

        try
        {
            _isSavingOverview = true;
            _overviewSavePending = false;
            OverviewSaveStatusLabel.Text = "Saving...";

            var updated = await App.State.Api.UpdatePackAsync(
                _pack.Id,
                new UpdatePackRequest(null, description, null, null, null, null, null, null, null, summary));

            _lastSavedSummary = summary;
            _lastSavedDescription = description;
            if (_customFormattedDescription && _descriptionEditing)
                _lastSavedDescriptionHtml = _currentDescriptionHtml;
            _pack = _pack with { Summary = summary, Description = description, UpdatedAt = updated.UpdatedAt };
            PackMetaLabel.Text = $"by {_pack.OwnerUsername} · updated {_pack.UpdatedAt.LocalDateTime:g}";
            SetPackSummary(summary);
            // The editor already shows this record's saved text, so a reload with the same
            // answer leaves it alone.
            _appliedSnapshot = Snapshot(_pack);
            OverviewSaveStatusLabel.Text = "Saved";
        }
        catch (Exception ex)
        {
            OverviewSaveStatusLabel.Text = "Save failed";
            StatusLabel.Text = "Overview save failed: " + ex.Message;
        }
        finally
        {
            _isSavingOverview = false;
            if (_overviewSavePending)
            {
                _overviewSavePending = false;
                _overviewSaveTimer.Start();
            }
        }
    }

    private string GetDescriptionExcerptForServer()
    {
        RefreshCurrentDescriptionHtml();
        if (_customFormattedDescription && _descriptionEditing)
            return PackText.ExcerptFromDescriptionHtml(_currentDescriptionHtml);

        if (_pack is not null && App.State.PackAssets.TryReadDescriptionHtml(_pack.Id, out var html))
            return PackText.ExcerptFromDescriptionHtml(html);

        return _lastSavedDescription;
    }

    private void OnExportDescription(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;

        var html = GetCurrentDescriptionHtmlForExport();
        var markdown = PackText.DescriptionHtmlToMarkdown(html);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            StatusLabel.Text = "This instance has no description to export.";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export description",
            FileName = SafeFileStem(_pack.Name) + ".md",
            Filter = "Markdown (*.md)|*.md|HTML (*.html)|*.html",
            DefaultExt = ".md",
            AddExtension = true
        };
        if (dlg.ShowDialog(ListOwnerWindow) != true) return;

        try
        {
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            var content = ext is ".html" or ".htm" ? html : markdown;
            File.WriteAllText(dlg.FileName, content);
            StatusLabel.Text = $"Exported description to {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Export failed: " + ex.Message;
        }
    }

    private async void OnImportDescription(object sender, RoutedEventArgs e)
    {
        if (_pack is null || !_isOwner) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import description",
            Filter = "Description files (*.md;*.markdown;*.txt;*.html;*.htm)|*.md;*.markdown;*.txt;*.html;*.htm"
                     + "|Markdown (*.md;*.markdown;*.txt)|*.md;*.markdown;*.txt"
                     + "|HTML (*.html;*.htm)|*.html;*.htm"
                     + "|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(ListOwnerWindow) != true) return;

        try
        {
            var raw = await File.ReadAllTextAsync(dlg.FileName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                StatusLabel.Text = "That file is empty.";
                return;
            }

            // .html/.htm are treated as HTML; otherwise sniff the content (markdown unless it
            // already looks like HTML). MarkdownLiteToHtml turns [label](/command) links and
            // bare /command lines into command blocks, so commands survive the import.
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            var isMarkdown = ext is not (".html" or ".htm") && !PackText.LooksLikeHtml(raw);

            var html = NormalizeDescriptionSource(raw, isMarkdown);
            if (string.IsNullOrWhiteSpace(html))
            {
                StatusLabel.Text = "Nothing to import from that file.";
                return;
            }

            App.State.PackAssets.SaveCustomDescriptionHtml(_pack.Id, html);

            // Reload the overview so the editor reflects the imported content, then persist
            // the excerpt + current summary to the server via the normal save path.
            _suppressEvents = true;
            try { ApplyDescriptionDisplay(_pack.Id, PackText.ExcerptFromDescriptionHtml(html)); }
            finally { _suppressEvents = false; }

            UpdateOverviewCharacterCount();
            await SaveOverviewDescriptionAsync();
            StatusLabel.Text = $"Imported description from {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Import failed: " + ex.Message;
        }
    }

    private string GetCurrentDescriptionHtmlForExport()
    {
        // While the owner is editing, the live editor holds the freshest edits in canonical form.
        if (_descriptionEditing)
        {
            RefreshCurrentDescriptionHtml();
            if (!string.IsNullOrWhiteSpace(_currentDescriptionHtml))
                return _currentDescriptionHtml;
        }

        // Otherwise prefer the persisted canonical HTML (never the runnable hosted-window form).
        if (App.State.PackAssets.TryReadDescriptionHtml(_packId, out var stored) && !string.IsNullOrWhiteSpace(stored))
            return stored;

        if (_pack is not null && !string.IsNullOrWhiteSpace(_pack.Description))
            return PackText.PrepareDescriptionHtml(_pack.Description, isMarkdown: !PackText.LooksLikeHtml(_pack.Description));

        return "";
    }

    private static string SafeFileStem(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string((name ?? "description").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "description" : clean;
    }

    private void UpdateOverviewCharacterCount()
    {
        OverviewSummaryCountLabel.Text = $"{OverviewSummaryBox.Text.Length}/{PackText.SummaryMaxLength}";
        if ((!_customFormattedDescription && !_importedRichDescription) || !_descriptionEditing)
        {
            OverviewCharacterCountLabel.Visibility = Visibility.Collapsed;
            return;
        }

        OverviewCharacterCountLabel.Visibility = Visibility.Visible;
        var excerpt = PackText.ExcerptFromDescriptionHtml(_currentDescriptionHtml);
        OverviewCharacterCountLabel.Text = $"{excerpt.Length}/{PackText.DescriptionMaxLength} server excerpt";
    }

    private void SetPackSummary(string summary)
    {
        PackSummaryLabel.Text = ToCardSummary(summary);
        PackSummaryLabel.Visibility = string.IsNullOrWhiteSpace(summary)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static string ToCardSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return summary ?? "";
        return Regex.Replace(summary.Replace('\r', '\n'), @"\s*\n+\s*", " ").Trim();
    }

    private void ApplyHeroIcon(PackDetail pack)
    {
        var initial = string.IsNullOrWhiteSpace(pack.Name)
            ? "?"
            : char.ToUpperInvariant(pack.Name.TrimStart()[0]).ToString();
        HeroIconFallback.Text = initial;

        var icon = App.State.PackAssets.TryLoadIconImage(pack.Id);
        if (icon is null)
        {
            HeroIconImage.Source = null;
            HeroIconImage.Visibility = Visibility.Collapsed;
            HeroIconFallback.Visibility = Visibility.Visible;
            return;
        }

        HeroIconImage.Source = icon;
        HeroIconImage.Visibility = Visibility.Visible;
        HeroIconFallback.Visibility = Visibility.Collapsed;
    }

    private void ApplyDescriptionDisplay(Guid packId, string plainDescription)
    {
        if (_pack?.IsShared == true)
        {
            App.State.PackAssets.ApplyFromSharedFolder(packId);
            if (_isOwner)
                App.State.PackAssets.MirrorToSharedFolder(packId);
        }

        _importedRichDescription = App.State.PackAssets.IsImportedDescription(packId)
            && App.State.PackAssets.TryReadDescriptionForDisplay(packId, out _, out _);

        if (_importedRichDescription
            && App.State.PackAssets.TryReadDescriptionForDisplay(packId, out var importedContent, out var importedIsMarkdown))
        {
            _showRichDescription = true;
            _customFormattedDescription = false;
            _descriptionEditing = false;
            OverviewCharacterCountLabel.Visibility = Visibility.Collapsed;
            LoadDescriptionViewer(importedContent, importedIsMarkdown);
            UpdateRichDescriptionChrome();
            return;
        }

        _customFormattedDescription = true;
        _showRichDescription = true;
        _descriptionEditing = _isOwner && !_hostedInMinecraftWindow;

        string? content = null;
        var isMarkdown = false;
        if (App.State.PackAssets.TryReadCustomDescription(packId, out var localContent, out var localIsMarkdown))
        {
            content = localContent;
            isMarkdown = localIsMarkdown;
        }
        else if (!string.IsNullOrWhiteSpace(plainDescription))
        {
            content = plainDescription;
            isMarkdown = !PackText.LooksLikeHtml(plainDescription);
        }

        if (_descriptionEditing)
        {
            LoadDescriptionEditor(content, isMarkdown);
            OverviewCharacterCountLabel.Visibility = Visibility.Visible;
        }
        else
        {
            LoadDescriptionViewer(content, isMarkdown);
            OverviewCharacterCountLabel.Visibility = Visibility.Collapsed;
        }

        UpdateOverviewCharacterCount();
        UpdateRichDescriptionChrome();
    }

    private void LoadDescriptionEditor(string? content, bool isMarkdown)
    {
        _lastSavedDescriptionHtml = NormalizeDescriptionSource(content, isMarkdown);
        _currentDescriptionHtml = _lastSavedDescriptionHtml;
        OverviewDescriptionEditor.ShowEditor(content, isMarkdown, OnDescriptionEditorChanged);
    }

    private static string NormalizeDescriptionSource(string? content, bool isMarkdown)
    {
        if (string.IsNullOrWhiteSpace(content)) return "";

        if (isMarkdown || !PackText.LooksLikeHtml(content))
            return PackText.NormalizeEditorHtmlForSave(PackText.MarkdownLiteToHtml(content));

        return PackText.NormalizeEditorHtmlForSave(PackText.SanitizeDescriptionHtml(content) ?? content);
    }

    private void LoadDescriptionViewer(string? content, bool isMarkdown)
    {
        OverviewDescriptionEditor.ShowViewer(content, isMarkdown, CreateDescriptionOptions());
        _currentDescriptionHtml = PackText.NormalizeEditorHtmlForSave(
            PackText.PrepareDescriptionHtml(content, isMarkdown, runnableCommands: _hostedInMinecraftWindow));
    }

    private RichDescriptionOptions CreateDescriptionOptions()
    {
        if (!_hostedInMinecraftWindow)
            return new RichDescriptionOptions();

        return new RichDescriptionOptions
        {
            EnableCommandRun = true,
            OnCommandRun = command =>
            {
                if (Window.GetWindow(this) is MinecraftHostWindow host)
                    host.RunMinecraftCommand(command);
            }
        };
    }

    private void UpdateRichDescriptionChrome()
    {
        if (!_showRichDescription)
        {
            OverviewDescriptionHost.Visibility = Visibility.Collapsed;
            OverviewDescriptionEditor.Visibility = Visibility.Collapsed;
            return;
        }

        var onOverview = _currentTab == DetailTabKind.Overview;
        OverviewDescriptionHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewDescriptionEditor.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Keeps a wheel over the description from also scrolling the page behind it. WebView2 scrolls
    /// itself, so this only marks the event handled.
    /// </summary>
    private void OnPagePreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_showRichDescription || _currentTab != DetailTabKind.Overview) return;
        if (OverviewDescriptionHost.Visibility != Visibility.Visible) return;
        if (OverviewDescriptionHost.ActualWidth <= 0 || OverviewDescriptionHost.ActualHeight <= 0) return;

        var pos = e.GetPosition(OverviewDescriptionHost);
        if (pos.X < 0 || pos.Y < 0 || pos.X > OverviewDescriptionHost.ActualWidth || pos.Y > OverviewDescriptionHost.ActualHeight)
            return;

        e.Handled = true;
    }

    private void WireOverviewTextBox(TextBox box)
    {
        box.ContextMenu = new ContextMenu();
        box.PreviewMouseRightButtonDown += OnOverviewTextBoxRightClick;
        box.ContextMenu.Opened += (_, _) => PopulateOverviewContextMenu(box);
    }

    private void OnOverviewTextBoxRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox box) return;

        var index = box.GetCharacterIndexFromPoint(e.GetPosition(box), snapToText: false);
        if (index >= 0)
        {
            box.Tag = FindOverviewLinkAt(box.Text, index);
            if (box.SelectionLength == 0 && !box.IsReadOnly)
                box.CaretIndex = Math.Clamp(index, 0, box.Text.Length);
        }
        else
        {
            box.Tag = null;
        }
    }

    private void PopulateOverviewContextMenu(TextBox box)
    {
        if (box.ContextMenu is null) return;

        var menu = box.ContextMenu;
        menu.Items.Clear();

        var link = box.Tag as string;
        if (!string.IsNullOrWhiteSpace(link))
        {
            menu.Items.Add(MenuItemWithIcon("Open link", "\uE774", () => OpenOverviewLink(link)));
            menu.Items.Add(MenuItemWithIcon("Copy link", "\uE8C8", () => Services.ClipboardHelper.TrySetText(link)));
            menu.Items.Add(new Separator());
        }

        var canEdit = _isOwner && !box.IsReadOnly;
        if (canEdit)
        {
            menu.Items.Add(MenuItemWithIcon("Cut", "\uE8C6", box.Cut, box.SelectionLength > 0));
        }
        menu.Items.Add(MenuItemWithIcon("Copy", "\uE8C8", box.Copy, box.SelectionLength > 0));
        if (canEdit)
        {
            menu.Items.Add(MenuItemWithIcon("Paste", "\uE77F", box.Paste, Clipboard.ContainsText()));
        }
        menu.Items.Add(MenuItemWithIcon("Select all", "\uE762", box.SelectAll, box.Text.Length > 0));

        if (canEdit)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItemWithIcon("Clear field", "\uE74D", box.Clear, box.Text.Length > 0));
        }
    }

    private MenuItem MenuItemWithIcon(string label, string glyph, Action action, bool isEnabled = true)
    {
        var item = new MenuItem
        {
            Header = label,
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 13,
                Width = 18,
                TextAlignment = TextAlignment.Center,
                Foreground = (Brush)FindResource("TextSecondaryBrush")
            },
            IsEnabled = isEnabled
        };
        item.Click += (_, _) => action();
        return item;
    }

    private static string? FindOverviewLinkAt(string text, int index)
    {
        if (string.IsNullOrEmpty(text) || index < 0 || index > text.Length) return null;

        foreach (Match match in OverviewLinkRegex.Matches(text))
        {
            if (index < match.Index || index > match.Index + match.Length) continue;
            return TrimOverviewLink(match.Value);
        }
        return null;
    }

    private static string TrimOverviewLink(string link)
    {
        return link.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
    }

    /// <summary>Follows a link in the overview text. The overview is written by whoever owns the
    /// instance, so only web and mail links are followed.</summary>
    private void OpenOverviewLink(string link)
    {
        bool opened;
        if (link.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            opened = SafeLaunch.OpenUrl("https://" + link);
        else if (link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            opened = OpenMailLink(link["mailto:".Length..]);
        else if (!link.Contains(':', StringComparison.Ordinal) && link.Contains('@', StringComparison.Ordinal))
            opened = SafeLaunch.OpenMail(link);
        else
            opened = SafeLaunch.OpenUrl(link);

        if (!opened) StatusLabel.Text = "Could not open that link.";
    }

    /// <summary>Opens the part of a mailto: link after the scheme, keeping its subject and body and
    /// dropping any other field, such as a mail app's attachment parameter.</summary>
    private static bool OpenMailLink(string mailto)
    {
        var query = mailto.IndexOf('?');
        var address = Uri.UnescapeDataString(query < 0 ? mailto : mailto[..query]);
        if (address.Length == 0) return false;

        string? subject = null, body = null;
        if (query >= 0)
        {
            foreach (var pair in mailto[(query + 1)..].Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                if (pair[..eq].Equals("subject", StringComparison.OrdinalIgnoreCase)) subject = value;
                else if (pair[..eq].Equals("body", StringComparison.OrdinalIgnoreCase)) body = value;
            }
        }
        return SafeLaunch.OpenMail(address, subject, body);
    }

    /// <summary>
    /// Syncs the timer and the manual Apply rules button with the auto-apply state: when on, the
    /// button is hidden and the safety-net timer runs; when off, the reverse.
    /// </summary>
    private void UpdateAutoApplyUi()
    {
        var auto = AutoApplyBox.IsChecked == true && _pack?.IsShared == true;
        ApplyRulesButton.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        if (auto && !_autoApplyTimer.IsEnabled) _autoApplyTimer.Start();
        else if (!auto && _autoApplyTimer.IsEnabled) _autoApplyTimer.Stop();
        AutoApplyLabel.Text = auto ? "On - rules apply automatically." : "";
    }

    /// <summary>Apply rules immediately if auto-apply is enabled for this pack.</summary>
    private void ApplyRulesIfAuto()
    {
        if (_pack is null || !_pack.IsShared) return;
        if (App.State.Settings.GetAutoApplyRulesFor(_pack.Id))
            _ = ApplyRulesCoreAsync(silent: true);
    }

    private static IEnumerable<TagVm> BuildTags(PackDetail p)
    {
        var rs = App.Current.Resources;
        TagVm tag(string text, string bgKey, string fgKey) =>
            new(text, (Brush)rs[bgKey], (Brush)rs[fgKey]);

        if (p.IsEmpty) yield return tag("EMPTY", "TagEmptyBgBrush", "TagEmptyFgBrush");
        else if (p.IsShared) yield return p.Visibility switch
        {
            PackVisibility.Public => tag("PUBLIC", "TagPublicBgBrush", "TagPublicFgBrush"),
            PackVisibility.Team   => tag("TEAM",   "TagTeamBgBrush",   "TagTeamFgBrush"),
            _                     => tag("SHARED", "TagSharedBgBrush", "TagSharedFgBrush")
        };
        else yield return tag("LOCAL", "TagEmptyBgBrush", "TagEmptyFgBrush");

        if (p.Loader != LoaderKind.None && !p.IsEmpty)
            yield return tag(p.Loader.ToString().ToUpperInvariant(), "TagSharedBgBrush", "TagSharedFgBrush");
    }

    // ── update detection ────────────────────────────────────────────────────

    /// <summary>
    /// Shows the Update button if the server has a newer shared-instance version than the local one.
    /// A pack that was never downloaded (locallySynced == 0) starts its first download automatically.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        if (_pack is null || !_pack.IsShared) return;
        if (!_pack.EffectivePermissions.HasFlag(PackPermissions.Download)) return;

        try
        {
            var manifest = await App.State.Api.GetManifestAsync(_pack.Id);
            var locallySynced = App.State.Settings.PackSyncedVersion.TryGetValue(_pack.Id, out var v) ? v : 0;
            var updateAvailable = manifest.Version > locallySynced;

            if (!updateAvailable)
            {
                UpdateButton.Visibility = Visibility.Collapsed;
                return;
            }

            // First-ever download: pull server content automatically instead of surfacing the button.
            if (locallySynced == 0 && manifest.Entries.Count > 0)
            {
                StatusLabel.Text = "Downloading server content...";
                if (!await RunSharedDownloadAsync(null)) return;
                ApplyHeroIcon(_pack);
                ApplyDescriptionDisplay(_pack.Id, _pack.Description ?? "");
                UpdateStatsLabel(_pack.Id);
                UpdateButton.Visibility = Visibility.Collapsed;
                _ = RefreshFileListsAsync();
                StatusLabel.Text = "Ready.";
                return;
            }

            UpdateButton.Visibility = Visibility.Visible;
        }
        catch { /* offline or auth issues, show nothing */ }
    }

    // ── file lists ───────────────────────────────────────────────────────────

    /// <summary>
    /// True when this user may change what the instance shares: the owner, or a collaborator with
    /// upload rights.
    /// </summary>
    /// <remarks>
    /// Only gates the sharing rules, which the next upload pushes to everyone. Rename, delete and new
    /// folder act on the user's own local files and are never gated.
    /// </remarks>
    private bool CanEditSharing =>
        _pack is not null &&
        (_isOwner || _pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared));

    /// <summary>
    /// The context-menu entries that write sharing rules, found by their <c>Tag</c>.
    /// </summary>
    /// <remarks>A ContextMenu on a UserControl belongs to that control's name scope, so <c>x:Name</c>
    /// inside these menus is an MC3093 build error. The tag is used instead.</remarks>
    private IEnumerable<MenuItem> SharingMenuItems()
    {
        foreach (var view in new[] { GameView, SharedView })
        {
            if (view.ContextMenu is not { } menu) continue;
            foreach (var entry in menu.Items)
                if (entry is MenuItem { Tag: "sharing" } item)
                    yield return item;
        }
    }

    private void ApplySharingPermissionsToFilesTab()
    {
        var may = CanEditSharing;
        const string why = "Only the instance owner and collaborators with upload rights can change "
                         + "what this instance shares.";

        foreach (var item in SharingMenuItems())
        {
            item.IsEnabled = may;
            ToolTipService.SetShowOnDisabled(item, true);
            if (!may) item.ToolTip = why;
        }

        EditRulesButton.IsEnabled = true; // read-only users may still look at the rules
        EditRulesButton.ToolTip = may
            ? "Configure auto-routing rules"
            : "See which files this instance shares. Editing the rules needs upload rights.";

        if (_pack is null) return;   // the recordless path already disabled it, with its own reason

        if (!may)
            DisableWithReason(ApplyRulesButton, why);
        else if (!_pack.IsShared)
            // Applying rules re-counts what would be uploaded, which is nothing until the instance is
            // published.
            DisableWithReason(ApplyRulesButton,
                "Rules decide what gets uploaded. Turn on Cloud sync in Options first.");
        else
        {
            ApplyRulesButton.IsEnabled = true;
            ApplyRulesButton.ToolTip = "Re-check every file against the rules";
        }
    }

    private void RefreshFileLists() => _ = RefreshFileListsAsync();

    private async Task RefreshFileListsAsync(bool runAutoApply = true)
    {
        // Keyed on the instance id alone, so the tab still works without a pack record (server down).
        var p = App.State.Packs;
        string packRoot, gameDir;
        try
        {
            // PackRoot throws if there is no folder and no name to make one. Nothing to list then,
            // and the exception must not go unobserved in the fire-and-forget wrapper.
            packRoot = p.PackRoot(_packId);
            gameDir = p.GameDir(_packId);
            Directory.CreateDirectory(gameDir);
        }
        catch (Exception ex)
        {
            AppLog.LogError("packs.file-list", ex);
            StatusLabel.Text = "This instance's folder could not be opened. "
                             + "The details are in the launcher log.";
            return;
        }
        var rules = App.State.Rules.Load(packRoot);

        _ = RefreshFileSummaryAsync(gameDir, rules);

        // Left: game/ view with rule badges. Shared files live in game/ on disk (the instance needs
        // them at launch) but are shown in the Shared column instead of here.
        GameView.Root  = gameDir;
        GameView.Rules = rules;
        GameView.MirrorLocalRoot  = null;
        GameView.MirrorSharedRoot = null;
        GameView.HideShared = _pack?.IsShared == true;

        // Right: the same game/ directory filtered to "shared"-rule files (what will be uploaded).
        // Always populated so marking a file shared shows at once; uploading still needs sharing on.
        SharedView.Root           = gameDir;
        SharedView.Rules          = rules;
        SharedView.ShowOnlyShared = true;
        SharedView.MirrorLocalRoot  = null;
        SharedView.MirrorSharedRoot = null;

        // Both views scan the same game/ tree, so run them concurrently.
        await Task.WhenAll(GameView.RefreshAsync(), SharedView.RefreshAsync());
    }

    // ── the Files tab's summary card ─────────────────────────────────────────

    /// <summary>
    /// Walks the instance folder once for the card above the file lists: how many files, how much
    /// disk, and how many of them sync.
    /// </summary>
    /// <remarks>
    /// Runs off the UI thread and is cancellable, since a modded instance with worlds can have six
    /// figures of files. A newer walk discards the older one's result. No placeholder count is shown.
    /// </remarks>
    private async Task RefreshFileSummaryAsync(string gameDir, List<PackRule> rules)
    {
        try { _fileSummaryCts?.Cancel(); } catch (ObjectDisposedException) { }
        var cts = new CancellationTokenSource();
        _fileSummaryCts = cts;
        var ct = cts.Token;

        // Keep the last measurement on screen next to the verb; a tab that never measured shows
        // only the verb, not a placeholder zero.
        FileSummaryLabel.Text = _lastFileSummary.Length == 0
            ? "Measuring this instance..."
            : _lastFileSummary + " · updating";

        try
        {
            var ruleService = App.State.Rules;
            var result = await Task.Run(() => MeasureInstance(gameDir, ruleService, rules, ct), ct);
            if (ct.IsCancellationRequested || !ReferenceEquals(_fileSummaryCts, cts)) return;

            if (result.Files == 0)
            {
                // The walk completed and found nothing.
                _lastFileSummary = "Nothing on disk yet - this instance has not been downloaded "
                                 + "or launched.";
            }
            else
            {
                var parts = new List<string>
                {
                    $"{result.Files:N0} file(s)",
                    FormatInstanceSize(result.Bytes)
                };
                if (result.Shared > 0) parts.Add($"{result.Shared:N0} marked shared");
                if (result.Unreadable > 0) parts.Add($"{result.Unreadable:N0} file(s) could not be read");
                _lastFileSummary = string.Join(" · ", parts);
            }
            FileSummaryLabel.Text = _lastFileSummary;
        }
        catch (OperationCanceledException) { /* a newer walk owns the label now */ }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_fileSummaryCts, cts)) return;
            AppLog.LogError("packs.file-summary", ex);
            _lastFileSummary = "";
            FileSummaryLabel.Text = "This instance's folder could not be measured. "
                                  + "The details are in the launcher log.";
        }
        finally
        {
            if (ReferenceEquals(_fileSummaryCts, cts)) _fileSummaryCts = null;
            cts.Dispose();
        }
    }

    private sealed record InstanceMeasurement(int Files, long Bytes, int Shared, int Unreadable);

    /// <summary>The last completed measurement's sentence, so a refresh keeps a number that is still
    /// true on screen instead of blanking it.</summary>
    private string _lastFileSummary = "";

    /// <summary>Formats a whole instance's size, up to GB. <see cref="ConfigHubService.FormatSize"/>
    /// stops at MB.</summary>
    private static string FormatInstanceSize(long bytes) => bytes switch
    {
        < 1024L * 1024 => ConfigHubService.FormatSize(bytes),
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.##} GB"
    };

    /// <summary>The off-thread half of <see cref="RefreshFileSummaryAsync"/>.</summary>
    private static InstanceMeasurement MeasureInstance(
        string gameDir, PackRuleService ruleService, List<PackRule> rules, CancellationToken ct)
    {
        var files = 0;
        long bytes = 0;
        var shared = 0;
        var unreadable = 0;

        var root = new DirectoryInfo(gameDir);
        if (!root.Exists) return new InstanceMeasurement(0, 0, 0, 0);

        var prefix = root.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            // Skip junctions: one into another instance would be counted as part of this one, and a
            // cycle would never end.
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };

        foreach (var file in root.EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            long length;
            try { length = file.Length; }
            catch (IOException) { unreadable++; continue; }
            catch (UnauthorizedAccessException) { unreadable++; continue; }

            files++;
            bytes += length;

            var rel = file.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? file.FullName[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/')
                : file.Name;

            var match = ruleService.Match(rel, rules);
            if (match.IsIgnored) continue;          // ignored files are not counted
            if (match.IsAutoShared) shared++;
        }

        return new InstanceMeasurement(files, bytes, shared, unreadable);
    }

    // ── options tab ──────────────────────────────────────────────────────────

    private void OnRamChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        RamValueLabel.Text = $"{(int)RamSlider.Value} MB";
    }

    private void OnRamCommit(object sender, MouseEventArgs e) => SaveRam();
    private void OnRamKeyUp(object sender, KeyEventArgs e) => SaveRam();

    private void SaveRam()
    {
        if (_suppressEvents || _pack is null) return;
        var mb = (int)RamSlider.Value;
        if (mb == App.State.Settings.DefaultMaxRamMb)
            App.State.Settings.SetMaxRamFor(_pack.Id, null);
        else
            App.State.Settings.SetMaxRamFor(_pack.Id, mb);
        App.State.Settings.Save();
    }

    private void OnJvmArgsCommit(object sender, RoutedEventArgs e) => SaveJvmArgs();

    private void OnJvmArgsKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveJvmArgs();
    }

    private void SaveJvmArgs()
    {
        if (_suppressEvents || _pack is null) return;
        App.State.Settings.SetJvmArgsFor(_pack.Id, JvmArgsBox.Text);
        App.State.Settings.Save();
    }

    // ── Java runtime picker ──────────────────────────────────────────────────

    private List<JavaChoice> _javaChoices = new();
    private bool _javaLoading;

    private async Task LoadJavaChoicesAsync(PackDetail pack)
    {
        _javaLoading = true;
        try
        {
            var required = LaunchService.RequiredJavaMajorFor(pack.MinecraftVersion);
            var launcherDefault = App.State.Settings.DefaultJavaPath;
            var autoLabel = string.IsNullOrWhiteSpace(launcherDefault) || !File.Exists(launcherDefault)
                ? $"Automatic  ·  Java {required} for Minecraft {pack.MinecraftVersion}"
                : $"Launcher default  ·  {launcherDefault}";
            var choices = await JavaPicker.BuildChoicesAsync(autoLabel);
            if (_pack?.Id != pack.Id) return;
            _javaChoices = choices;
            App.State.Settings.PackJavaPath.TryGetValue(pack.Id, out var current);
            JavaPicker.Apply(JavaBox, _javaChoices, current);
            UpdateJavaHint();
        }
        catch (Exception ex) { JavaHint.Text = "Could not list Java installations: " + ex.Message; }
        finally { _javaLoading = false; }
    }

    private void OnJavaChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_javaLoading || _suppressEvents || _pack is null) return;
        if (JavaBox.SelectedItem is not JavaChoice choice) return;

        if (choice.Kind == JavaChoiceKind.Browse)
        {
            var picked = JavaPicker.Browse(Window.GetWindow(this));
            if (picked is not null)
            {
                App.State.Settings.SetJavaPathFor(_pack.Id, picked);
                App.State.Settings.Save();
            }
            _ = LoadJavaChoicesAsync(_pack); // re-list (and re-select) either way
            return;
        }

        App.State.Settings.SetJavaPathFor(_pack.Id, choice.Path);
        App.State.Settings.Save();
        UpdateJavaHint();
    }

    private void UpdateJavaHint()
    {
        if (_pack is null) return;
        var path = App.State.Settings.GetJavaPathFor(_pack.Id);
        JavaHint.Text = path is null
            ? "Launches with the Java version this Minecraft release needs, downloaded automatically when it is missing. Applies on the next launch."
            : $"Launches with {path}. Applies on the next launch.";
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _pack is null) return;
        App.State.Settings.PackAutoUpdate[_pack.Id] = AutoUpdateBox.IsChecked == true;
        App.State.Settings.Save();
    }

    private void OnPackPageCollapsedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _pack is null) return;

        bool? value = PackPageCollapsedBox.SelectedIndex switch
        {
            1 => false,
            2 => true,
            _ => null
        };
        App.State.Settings.SetMinecraftWindowPackPageCollapsedOverride(_pack.Id, value);
        App.State.Settings.Save();
        StatusLabel.Text = value switch
        {
            null => "Launcher chrome uses the launcher default.",
            true => "Launcher chrome will start hidden for this instance.",
            false => "Launcher chrome will start visible for this instance."
        };
    }

    private void OnBorderlessFullscreenChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _pack is null) return;

        bool? value = BorderlessFullscreenBox.SelectedIndex switch
        {
            1 => false,
            2 => true,
            _ => null
        };
        App.State.Settings.SetMinecraftWindowBorderlessFullscreenOverride(_pack.Id, value);
        App.State.Settings.Save();
        StatusLabel.Text = value switch
        {
            null => "Borderless fullscreen uses the launcher default.",
            true => "This instance will start borderless fullscreen.",
            false => "This instance will start windowed."
        };
    }

    // ── rules / auto-apply ───────────────────────────────────────────────────

    private void OnEditRules(object sender, RoutedEventArgs e)
    {
        // The rules live in the instance folder, so they open without a server record too. Offline,
        // the dialog just never pushes them back up.
        var dlg = new PackRulesDialog(
            App.State.Packs.PackRoot(_packId),
            _pack?.Name ?? PackNameLabel.Text,
            packId: _pack?.IsShared == true ? _packId : null,
            isOwner: _isOwner) { Owner = _shell };
        dlg.ShowDialog();
        _ = RefreshFileListsAsync();
    }

    private void OnAutoApplyToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _pack is null) return;
        if (AutoApplyBox.IsChecked == true && !_pack.IsShared)
        {
            AutoApplyBox.IsChecked = false;
            StatusLabel.Text = "Turn on Cloud sync in Options first.";
            return;
        }
        App.State.Settings.SetAutoApplyRulesFor(_pack.Id, AutoApplyBox.IsChecked == true);
        UpdateAutoApplyUi();
        ApplyRulesIfAuto();
    }

    private void OnAutoApplyTick(object? sender, EventArgs e) => ApplyRulesIfAuto();

    private async void OnApplyRules(object sender, RoutedEventArgs e)
    {
        if (_pack is null || !_pack.IsShared)
        {
            StatusLabel.Text = "Turn on Cloud sync in Options first.";
            return;
        }
        var (_, message) = await ApplyRulesCoreAsync(silent: false);
        StatusLabel.Text = message;
    }

    /// <summary>
    /// Scans game/ against the current rules and refreshes the sync-preview pane (SharedView).
    /// Files are not moved; the rules only decide which game/ files the next sync uploads.
    /// </summary>
    private async Task<(int matched, string message)> ApplyRulesCoreAsync(bool silent, bool refreshAfter = true)
    {
        if (_pack is null || !_pack.IsShared) return (0, "");

        var packId = _packId;
        var packRoot = App.State.Packs.PackRoot(packId);
        var rules = App.State.Rules.Load(packRoot);
        var gameDir = App.State.Packs.GameDir(packId);

        var matchedShared = await Task.Run(() =>
        {
            var allGameFiles = App.State.Packs.ListRelativeFiles(gameDir);
            var annotated = App.State.Rules.Apply(allGameFiles, rules);
            return annotated.Count(t => t.Match.IsAutoShared);
        });

        if (refreshAfter)
            await RefreshFileListsAsync(runAutoApply: false);

        var message = matchedShared > 0
            ? $"{matchedShared} file(s) in game/ will sync (per rules)."
            : "No files in game/ matched any shared rule.";

        return (matchedShared, message);
    }

    private void OnQuickRuleShared(object sender, RoutedEventArgs e)  => AddQuickRule(RuleAction.Shared,  folderMode: false);
    private void OnQuickRuleIgnored(object sender, RoutedEventArgs e) => AddQuickRule(RuleAction.Ignored, folderMode: false);
    private void OnQuickRuleFolder(object sender, RoutedEventArgs e)  => AddQuickRule(RuleAction.Shared,  folderMode: true);

    private void RemoveFromShared(IReadOnlyList<string> relativePaths)
    {
        if (_pack is null || relativePaths.Count == 0) return;
        var packRoot = App.State.Packs.PackRoot(_packId);
        var gameDir = App.State.Packs.GameDir(_packId);
        var rules = App.State.Rules.Load(packRoot);
        foreach (var rel in relativePaths)
        {
            // A folder needs a trailing-slash pattern so the Local rule covers its contents. Inserted
            // first (first match wins) so it overrides a broader parent rule like "mods/" -> shared.
            var isDir = Directory.Exists(Path.Combine(gameDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            var pattern = isDir ? rel.TrimEnd('/') + "/" : rel;
            rules.RemoveAll(r => string.Equals(r.Pattern.TrimEnd('/'), pattern.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            rules.Insert(0, new PackRule { Pattern = pattern, Action = RuleAction.Local });
        }
        App.State.Rules.Save(packRoot, rules);
        StatusLabel.Text = $"Removed {relativePaths.Count} file(s) from sync.";
        _ = RefreshFileListsAsync(runAutoApply: false);
    }

    private void AddQuickRule(RuleAction action, bool folderMode)
    {
        if (_pack is null) return;
        var selectedEntries = GameView.GetSelectedEntries();
        if (selectedEntries.Count == 0) { StatusLabel.Text = "Select a file first."; return; }

        var packRoot = App.State.Packs.PackRoot(_packId);
        var rules = App.State.Rules.Load(packRoot);

        foreach (var entry in selectedEntries)
        {
            var path = entry.RelativePath;
            string pattern;
            if (folderMode)
            {
                var parts = path.Split('/');
                pattern = parts.Length > 1 ? parts[0] + "/" : path;
            }
            // A folder's rule must end in '/' so the matcher expands it to '/**' and covers its
            // contents; without the slash it only matches the folder name itself.
            else pattern = entry.IsFolder ? path.TrimEnd('/') + "/" : path;

            // Match ignoring a trailing slash so re-marking a folder also clears any stale
            // slash-less rule for the same name (otherwise both "scripts" and "scripts/" pile up).
            rules.RemoveAll(r => string.Equals(r.Pattern.TrimEnd('/'), pattern.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            rules.Insert(0, new PackRule { Pattern = pattern, Action = action });
        }

        App.State.Rules.Save(packRoot, rules);
        var count = selectedEntries.Count;
        StatusLabel.Text = action switch
        {
            RuleAction.Shared  => $"Marked {count} item(s) as shared.",
            RuleAction.Ignored => $"Marked {count} item(s) as ignored.",
            _                  => $"Added rule for {count} item(s)."
        };
        _ = RefreshFileListsAsync(runAutoApply: false);
    }

    // ── mod loader ──────────────────────────────────────────────────────────

    /// <summary>Token for the in-flight loader-version fetch, so a fast loader switch can't
    /// have a slow earlier response overwrite the list for the loader now selected.</summary>
    private CancellationTokenSource? _loaderVersionCts;

    /// <summary>The list refresh kicked off by the last <see cref="ApplyLoaderCard"/>, so a save can
    /// wait for it before writing its confirmation into the same status line.</summary>
    private Task? _loaderVersionRefresh;

    private void ApplyLoaderCard(PackDetail pack)
    {
        // Only the owner may change this (the server rejects everyone else), and an empty
        // instance has no Minecraft version to pick builds for.
        var editable = _isOwner && !pack.IsEmpty && !string.IsNullOrEmpty(pack.MinecraftVersion);
        LoaderCard.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        if (!editable) return;

        LoaderMcVersionLabel.Text = pack.MinecraftVersion;
        foreach (ComboBoxItem item in PackLoaderBox.Items)
            if (item.Tag as string == pack.Loader.ToString())
                PackLoaderBox.SelectedItem = item;

        LoaderStatusLabel.Text = "";
        ApplyLoaderButton.IsEnabled = false;
        _loaderVersionRefresh = RefreshPackLoaderVersionsAsync(pack.LoaderVersion);
    }

    private LoaderKind SelectedPackLoader() =>
        PackLoaderBox.SelectedItem is ComboBoxItem item && item.Tag is string tag
        && Enum.TryParse<LoaderKind>(tag, out var kind)
            ? kind
            : LoaderKind.None;

    /// <summary>Fills the version list for the selected loader, preselecting
    /// <paramref name="preferred"/> when it's still offered.</summary>
    private async Task RefreshPackLoaderVersionsAsync(string? preferred)
    {
        if (_pack is null || string.IsNullOrEmpty(_pack.MinecraftVersion)) return;
        var loader = SelectedPackLoader();

        _loaderVersionCts?.Cancel();
        var cts = _loaderVersionCts = new CancellationTokenSource();

        PackLoaderVersionBox.ItemsSource = null;
        PackLoaderVersionRow.IsEnabled = loader != LoaderKind.None;
        if (loader == LoaderKind.None)
        {
            UpdateApplyLoaderState();
            return;
        }

        LoaderStatusLabel.Text = "Loading builds...";
        try
        {
            var versions = await App.State.Versions.ListLoaderVersionsAsync(loader, _pack.MinecraftVersion, cts.Token);
            if (cts.IsCancellationRequested) return;

            PackLoaderVersionBox.ItemsSource = versions;
            if (versions.Count == 0)
            {
                LoaderStatusLabel.Text = $"{loader} publishes no builds for Minecraft {_pack.MinecraftVersion}.";
            }
            else
            {
                // Keep the current build selected when it's still on offer, so opening the card
                // and pressing Apply can't silently move the instance to the newest build.
                var index = preferred is null ? -1 : versions.IndexOf(preferred);
                PackLoaderVersionBox.SelectedIndex = index >= 0 ? index : 0;
                LoaderStatusLabel.Text = "";
            }
        }
        catch (OperationCanceledException) { /* superseded by a newer selection */ }
        catch (Exception ex)
        {
            // Show a plain message instead of the socket error; the details go to the log.
            if (!cts.IsCancellationRequested)
            {
                AppLog.Log("loader", "Loader builds could not be listed: " + ex.Message);
                LoaderStatusLabel.Text = "Couldn't list the builds: "
                    + (Connectivity.DescribeTransportFailure(ex, cts.Token) ?? "the list could not be read") + ".";
            }
        }
        finally
        {
            if (!cts.IsCancellationRequested) UpdateApplyLoaderState();
        }
    }

    /// <summary>Apply is offered only when the pending choice is complete and differs from
    /// what's saved.</summary>
    private void UpdateApplyLoaderState()
    {
        if (_pack is null) { ApplyLoaderButton.IsEnabled = false; return; }
        var loader = SelectedPackLoader();
        var version = PackLoaderVersionBox.SelectedItem as string;

        var complete = loader == LoaderKind.None || !string.IsNullOrEmpty(version);
        var changed = loader != _pack.Loader
                      || (loader != LoaderKind.None && version != _pack.LoaderVersion);
        ApplyLoaderButton.IsEnabled = complete && changed;
    }

    private async void OnPackLoaderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _pack is null) return;
        // Returning to the saved loader should offer its saved build back, not the newest one.
        var preferred = SelectedPackLoader() == _pack.Loader ? _pack.LoaderVersion : null;
        await RefreshPackLoaderVersionsAsync(preferred);
    }

    private void OnPackLoaderVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _pack is null) return;
        UpdateApplyLoaderState();
    }

    private async void OnApplyLoaderChange(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _pack is null) return;
        var loader = SelectedPackLoader();
        var version = loader == LoaderKind.None ? null : PackLoaderVersionBox.SelectedItem as string;
        if (loader != LoaderKind.None && string.IsNullOrEmpty(version))
        {
            LoaderStatusLabel.Text = "Pick a build first.";
            return;
        }

        ApplyLoaderButton.IsEnabled = false;
        LoaderStatusLabel.Text = "Saving...";
        try
        {
            // The server leaves a field alone when it arrives null, so clearing the build for a
            // vanilla instance has to be sent as an empty string rather than null.
            var updated = await App.State.Api.UpdatePackAsync(
                _pack.Id,
                new UpdatePackRequest(null, null, null, null, null, null, loader, version ?? ""));
            _shell.AddOrUpdatePackList(updated); // keep the instance card's subtitle in step
            await ReloadAsync();
            // The reload clears this status line when it lands, so wait for it before confirming.
            if (_loaderVersionRefresh is { } refresh) await refresh;
            LoaderStatusLabel.Text = loader == LoaderKind.None
                ? "Now vanilla - takes effect next launch."
                : $"Now {loader} {version} - installs on next launch.";
        }
        catch (Exception ex)
        {
            LoaderStatusLabel.Text = "Save failed: " + ex.Message;
            UpdateApplyLoaderState();
        }
    }

    // ── sharing tab ─────────────────────────────────────────────────────────

    private void OnRefreshFiles(object sender, RoutedEventArgs e) => RefreshFileLists();

    private async void OnVisibilityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || _pack is null) return;
        var v = (PackVisibility)VisibilityBox.SelectedIndex;
        if (v == _pack.Visibility) return;
        try
        {
            await App.State.Api.UpdatePackAsync(_pack.Id, new UpdatePackRequest(null, null, v, null, null, null, null, null));
            await ReloadAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private async void OnIsSharedToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _pack is null) return;
        var nowShared = IsSharedBox.IsChecked == true;
        if (nowShared == _pack.IsShared) return;
        try
        {
            await App.State.Api.UpdatePackAsync(_pack.Id, new UpdatePackRequest(null, null, null, nowShared, null, null, null, null));
            if (nowShared)
                App.State.PackAssets.MirrorToSharedFolder(_pack.Id);
            await ReloadAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnEditPermissions(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        new PermissionsDialog(_pack) { Owner = _shell }.ShowDialog();
        _ = ReloadAsync();
    }

    private void OnOpenGame(object sender, RoutedEventArgs e) =>
        OpenInExplorer(App.State.Packs.GameDir(_packId));
    private void OnOpenShared(object sender, RoutedEventArgs e) =>
        OpenInExplorer(App.State.Packs.GameDir(_packId)); // game/ is the sync source
    private void OnOpenServerOverride(object sender, RoutedEventArgs e) =>
        OpenInExplorer(Path.Combine(App.State.Packs.PackRoot(_packId), "server"));

    private void OnRevealGame(object sender, RoutedEventArgs e)   => RevealSelected(GameView, App.State.Packs.GameDir(_packId));
    private void OnRevealShared(object sender, RoutedEventArgs e) => RevealSelected(SharedView, App.State.Packs.GameDir(_packId));

    private static void RevealSelected(FolderView view, string root)
    {
        var sel = view.GetSelectedFiles();
        var dir = sel.Count > 0
            ? Path.GetDirectoryName(Path.Combine(root, sel[0].Replace('/', Path.DirectorySeparatorChar))) ?? root
            : root;
        Directory.CreateDirectory(dir);
        SafeLaunch.OpenFolder(dir);
    }

    private static void OpenInExplorer(string path)
    {
        Directory.CreateDirectory(path);
        SafeLaunch.OpenFolder(path);
    }

    // ── Files tab: rename, delete, new folder, copy path ─────────────────────

    /// <summary>Shared column "Edit file": the same editor the Game column opens.</summary>
    private void OnEditSelectedSharedFile(object sender, RoutedEventArgs e)
    {
        var selected = SharedView.GetSelectedFiles().FirstOrDefault();
        if (selected is null) { StatusLabel.Text = "Select a file to edit."; return; }
        OpenInEditor(selected);
    }

    private void OnGameCopyPath(object sender, RoutedEventArgs e) => CopySelectedPath(GameView);
    private void OnSharedCopyPath(object sender, RoutedEventArgs e) => CopySelectedPath(SharedView);

    private void CopySelectedPath(FolderView view)
    {
        var entry = view.SelectedEntry;
        if (entry is null) { StatusLabel.Text = "Select a file first."; return; }
        var full = AbsolutePathFor(entry.RelativePath);
        StatusLabel.Text = ClipboardHelper.TrySetText(full) ? "Path copied." : "Could not reach the clipboard.";
    }

    private void OnGameRename(object sender, RoutedEventArgs e) => _ = RenameSelectedAsync(GameView);
    private void OnSharedRename(object sender, RoutedEventArgs e) => _ = RenameSelectedAsync(SharedView);
    private void OnGameDelete(object sender, RoutedEventArgs e) => _ = DeleteSelectedAsync(GameView);
    private void OnSharedDelete(object sender, RoutedEventArgs e) => _ = DeleteSelectedAsync(SharedView);
    private void OnGameNewFolder(object sender, RoutedEventArgs e) => _ = NewFolderAsync(GameView);

    /// <summary>Absolute path of a Files-tab entry. Both columns are views onto game/, so one
    /// mapping covers them both.</summary>
    private string AbsolutePathFor(string relativePath) =>
        Path.Combine(App.State.Packs.GameDir(_packId), relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Renames one file or folder in place. Single selection only; bulk rename would be a
    /// different feature.</summary>
    private async Task RenameSelectedAsync(FolderView view)
    {
        try
        {
            var entries = view.GetSelectedEntries();
            if (entries.Count == 0) { StatusLabel.Text = "Select a file or folder to rename."; return; }
            if (entries.Count > 1) { StatusLabel.Text = "Rename works on one item at a time."; return; }

            var entry = entries[0];
            var source = AbsolutePathFor(entry.RelativePath);
            var currentName = Path.GetFileName(source);
            var dlg = new SimpleInputDialog("Rename", entry.IsFolder ? "New folder name" : "New file name", currentName)
            {
                Owner = ListOwnerWindow
            };
            if (dlg.ShowDialog() != true) return;

            var name = (dlg.Result ?? "").Trim();
            if (name.Length == 0 || string.Equals(name, currentName, StringComparison.Ordinal)) return;
            if (name.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            {
                StatusLabel.Text = "That name has characters Windows will not allow in a file name.";
                return;
            }
            if (!PathSafety.IsSafeFileName(name))
            {
                StatusLabel.Text = "Windows will not allow that as a file name.";
                return;
            }

            var target = Path.Combine(Path.GetDirectoryName(source) ?? "", name);
            if (File.Exists(target) || Directory.Exists(target))
            {
                StatusLabel.Text = $"'{name}' already exists in that folder.";
                return;
            }

            // Rules match on the path, so renaming a shared file unshares it. Warn, or the next
            // upload will drop it from the server without notice.
            var wasShared = entry.IsAutoShared;

            if (entry.IsFolder) Directory.Move(source, target);
            else File.Move(source, target);

            StatusLabel.Text = wasShared
                ? $"Renamed to {name}. It no longer matches its shared rule - re-mark it if it should still sync."
                : $"Renamed to {name}.";
            await RefreshFileListsAsync(runAutoApply: false);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not rename that: " + ex.Message;
        }
    }

    /// <summary>Deletes the selected files and folders from the instance on disk.</summary>
    /// <remarks>Both columns show game/, so this is the same delete either way. The confirmation calls
    /// out shared files, since the next upload also removes them for every collaborator.</remarks>
    private async Task DeleteSelectedAsync(FolderView view)
    {
        if (_fileTransferInProgress) return;
        try
        {
            var entries = view.GetSelectedEntries();
            if (entries.Count == 0) { StatusLabel.Text = "Select something to delete."; return; }

            var sharedCount = entries.Count(en => en.IsAutoShared);
            var what = entries.Count == 1
                ? $"'{entries[0].DisplayName.TrimEnd('/')}'"
                : $"{entries.Count} items";
            var message = $"Delete {what} from this instance's game folder? This cannot be undone.";
            if (entries.Any(en => en.IsFolder))
                message += "\n\nFolders are deleted with everything inside them.";
            if (sharedCount > 0)
                message += $"\n\n{(sharedCount == entries.Count ? "They are" : $"{sharedCount} of them are")} marked shared - the next upload removes them from the server for everyone.";

            var ok = await AppDialog.ConfirmAsync(ListOwnerWindow, "Delete from instance", message,
                "Delete", "Cancel", danger: true);
            if (!ok) return;

            _fileTransferInProgress = true;
            StatusLabel.Text = "Deleting...";
            var paths = entries.Select(en => en.RelativePath).ToList();
            var root = App.State.Packs.GameDir(_packId);
            var (removed, error) = await Task.Run(() => DeleteEntriesFromRoot(root, paths));

            StatusLabel.Text = error is not null
                ? $"Deleted {removed} item(s), then failed: {error}"
                : $"Deleted {removed} item(s).";
            await RefreshFileListsAsync(runAutoApply: false);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Delete failed: " + ex.Message;
        }
        finally { _fileTransferInProgress = false; }
    }

    /// <summary>Creates a folder inside whichever folder the Game column is showing.</summary>
    private async Task NewFolderAsync(FolderView view)
    {
        try
        {
            var dlg = new SimpleInputDialog("New folder", "Folder name", "") { Owner = ListOwnerWindow };
            if (dlg.ShowDialog() != true) return;

            var name = (dlg.Result ?? "").Trim();
            if (name.Length == 0) return;
            if (name.Any(c => Path.GetInvalidFileNameChars().Contains(c)))
            {
                StatusLabel.Text = "That name has characters Windows will not allow in a folder name.";
                return;
            }
            if (!PathSafety.IsSafeFileName(name))
            {
                StatusLabel.Text = "Windows will not allow that as a folder name.";
                return;
            }

            var parentRel = view.CurrentRelativeDir;
            var parent = parentRel.Length == 0
                ? App.State.Packs.GameDir(_packId)
                : AbsolutePathFor(parentRel);
            var target = Path.Combine(parent, name);
            if (Directory.Exists(target)) { StatusLabel.Text = "That folder already exists."; return; }

            Directory.CreateDirectory(target);
            StatusLabel.Text = $"Created {name}/.";
            await RefreshFileListsAsync(runAutoApply: false);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not create that folder: " + ex.Message;
        }
    }

    /// <summary>
    /// Copies files dragged in from Explorer into whichever folder the column is showing. Dropping onto
    /// the Shared column also marks them shared.
    /// </summary>
    private async void OnExternalFilesDropped(FolderView view, string destRoot, string destRelativeDir, IReadOnlyList<string> sources)
    {
        // Copying local files needs nothing from the server, so this does not wait for an instance
        // record. Only the mark-as-shared part below does.
        if (_fileTransferInProgress) return;

        _fileTransferInProgress = true;
        try
        {
            var destDir = destRelativeDir.Length == 0
                ? destRoot
                : Path.Combine(destRoot, destRelativeDir.Replace('/', Path.DirectorySeparatorChar));

            StatusLabel.Text = $"Copying {sources.Count} item(s) in...";
            var paths = sources.ToList();
            var (copied, relatives, error) = await Task.Run(() => CopyExternalEntries(destRoot, destDir, paths));

            if (error is not null)
            {
                StatusLabel.Text = $"Copied {copied} item(s), then failed: {error}";
            }
            else
            {
                StatusLabel.Text = $"Copied {copied} item(s) into {(destRelativeDir.Length == 0 ? "game/" : destRelativeDir + "/")}.";
            }

            // Only the sync-preview column means "share this"; a drop on the Game column just copies.
            var markShared = view.ShowOnlyShared && relatives.Count > 0 && CanEditSharing;
            if (markShared)
            {
                MarkPathsShared(relatives);
                StatusLabel.Text += " Marked shared.";
            }

            await RefreshFileListsAsync(runAutoApply: false);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not copy those files in: " + ex.Message;
        }
        finally { _fileTransferInProgress = false; }
    }

    /// <summary>Copies dropped files and folders into <paramref name="destDir"/>, returning their
    /// paths relative to <paramref name="destRoot"/> so the caller can write rules for them.</summary>
    private static (int copied, List<string> relatives, string? error) CopyExternalEntries(
        string destRoot, string destDir, IReadOnlyList<string> sources)
    {
        var copied = 0;
        var relatives = new List<string>();
        try
        {
            Directory.CreateDirectory(destDir);
            foreach (var source in sources)
            {
                if (Directory.Exists(source))
                {
                    var folderName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
                    var targetFolder = Path.Combine(destDir, folderName);
                    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        var target = Path.Combine(targetFolder, Path.GetRelativePath(source, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: true);
                        relatives.Add(Path.GetRelativePath(destRoot, target).Replace('\\', '/'));
                        copied++;
                    }
                }
                else if (File.Exists(source))
                {
                    var target = Path.Combine(destDir, Path.GetFileName(source));
                    File.Copy(source, target, overwrite: true);
                    relatives.Add(Path.GetRelativePath(destRoot, target).Replace('\\', '/'));
                    copied++;
                }
            }
            return (copied, relatives, null);
        }
        catch (Exception ex) { return (copied, relatives, ex.Message); }
    }

    /// <summary>Adds a "shared" rule for each path, replacing any rule already covering it.</summary>
    private void MarkPathsShared(IReadOnlyList<string> relativePaths)
    {
        var packRoot = App.State.Packs.PackRoot(_packId);
        var rules = App.State.Rules.Load(packRoot);
        foreach (var rel in relativePaths)
        {
            rules.RemoveAll(r => string.Equals(r.Pattern.TrimEnd('/'), rel.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            rules.Insert(0, new PackRule { Pattern = rel, Action = RuleAction.Shared });
        }
        App.State.Rules.Save(packRoot, rules);
    }

    // ── moves ────────────────────────────────────────────────────────────────

    // "Copy to shared" adds a "shared" rule so the file is included in the next upload.
    private void OnMoveGameToShared(object sender, RoutedEventArgs e) =>
        AddQuickRule(RuleAction.Shared, folderMode: false);

    // "Remove from shared" marks the file ignored/local so it is excluded from uploads.
    private void OnMoveSharedToGame(object sender, RoutedEventArgs e) =>
        RemoveFromShared(SharedView.GetSelectedEntries().Select(e => e.RelativePath).ToList());

    private async Task TransferEntriesAsync(string sourceRoot, string destRoot, IReadOnlyList<string> relativePaths)
    {
        if (_pack is null || _fileTransferInProgress) return;

        _fileTransferInProgress = true;
        StatusLabel.Text = "Moving files...";
        try
        {
            var paths = relativePaths.ToList();
            var (transferred, error) = await Task.Run(() => TransferEntriesCore(sourceRoot, destRoot, paths, keepSourceCopy: false));
            if (error is not null) { StatusLabel.Text = "Move failed: " + error; return; }
            StatusLabel.Text = $"Moved {transferred} item(s).";
            await RefreshFileListsAsync();
        }
        finally
        {
            _fileTransferInProgress = false;
        }
    }

    private static (int transferred, string? error) TransferEntriesCore(
        string sourceRoot,
        string destRoot,
        IReadOnlyList<string> relativePaths,
        bool keepSourceCopy)
    {
        var transferred = 0;
        try
        {
            foreach (var rel in relativePaths)
            {
                if (PathSafety.ResolveInside(sourceRoot, rel) is not { } srcAbs)
                {
                    AppLog.Log(nameof(PackDetailView), $"Skipped moving an entry that is not a path inside the instance: {rel}");
                    continue;
                }
                if (Directory.Exists(srcAbs))
                {
                    foreach (var file in Directory.EnumerateFiles(srcAbs, "*", SearchOption.AllDirectories))
                    {
                        var inner = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
                        if (keepSourceCopy)
                            App.State.Packs.CopyFileBetweenFolders(sourceRoot, destRoot, inner);
                        else
                            App.State.Packs.MoveFileBetweenFolders(sourceRoot, destRoot, inner);
                        transferred++;
                    }
                    if (!keepSourceCopy)
                        try { Directory.Delete(srcAbs, recursive: true); } catch { }
                }
                else if (File.Exists(srcAbs))
                {
                    if (keepSourceCopy)
                        App.State.Packs.CopyFileBetweenFolders(sourceRoot, destRoot, rel);
                    else
                        App.State.Packs.MoveFileBetweenFolders(sourceRoot, destRoot, rel);
                    transferred++;
                }
            }

            return (transferred, null);
        }
        catch (Exception ex)
        {
            return (transferred, ex.Message);
        }
    }

    private static (int removed, string? error) DeleteEntriesFromRoot(string root, IReadOnlyList<string> relativePaths)
    {
        var removed = 0;
        try
        {
            foreach (var rel in relativePaths)
            {
                if (PathSafety.ResolveInside(root, rel) is not { } abs)
                {
                    AppLog.Log(nameof(PackDetailView), $"Skipped deleting an entry that is not a path inside the instance: {rel}");
                    continue;
                }
                if (Directory.Exists(abs))
                {
                    Directory.Delete(abs, recursive: true);
                    removed++;
                }
                else if (File.Exists(abs))
                {
                    File.Delete(abs);
                    removed++;
                }
            }
            return (removed, null);
        }
        catch (Exception ex) { return (removed, ex.Message); }
    }

    private bool IsGameRoot(string root) =>
        string.Equals(
            Path.GetFullPath(root),
            Path.GetFullPath(App.State.Packs.GameDir(_packId)),
            StringComparison.OrdinalIgnoreCase);

    // ── upload / download ────────────────────────────────────────────────────

    /// <summary>
    /// While a transfer is running the button that started it becomes its cancel button.
    /// </summary>
    private void SetSyncUiRunning(bool running, bool downloading)
    {
        if (downloading)
        {
            UpdateButtonGlyph.Text = running ? "" : "";
            // Only the Update button's glyph is set here. Its label, enabled state and tooltip come
            // from UpdateTransferControls on PackJobs.Changed, which also covers other transfers.
            UploadButton.IsEnabled = !running && _pack is { IsShared: true }
                                     && _pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared);
            return;
        }

        UploadButtonGlyph.Text = running ? "" : "";
        UploadButtonText.Text = running ? "Cancel upload" : "Upload to server";
        UploadButton.ToolTip = running ? "Stop the upload. Nothing is committed until it finishes." : null;
        UpdateButton.IsEnabled = !running;
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;

        // Second click on a running upload means stop.
        if (_syncCts is not null) { CancelSync("Cancelling upload..."); return; }

        LogBox.Text = "";
        var progress = new Progress<string>(line => AppendCapped(LogBox, line + Environment.NewLine));
        var cts = new CancellationTokenSource();
        _syncCts = cts;
        SetSyncUiRunning(true, downloading: false);
        try
        {
            if (_isOwner)
                await SaveOverviewDescriptionAsync();

            // Ensure pack assets (icon, description) are staged in game/.cloudlauncher/
            App.State.PackAssets.MirrorToSharedFolder(_pack.Id);

            // Determine which files in game/ to include: those matching "shared" rules,
            // plus any .cloudlauncher/ assets.
            var packRoot = App.State.Packs.PackRoot(_pack.Id);
            var rules = App.State.Rules.Load(packRoot);
            var gameDir = App.State.Packs.GameDir(_pack.Id);
            var sharedPaths = await Task.Run(() => CollectSharedPaths(gameDir, rules), cts.Token);

            // Base the upload on the version this client last synced, not the server's current one,
            // so the server answers 409 when we are behind and forces a download first, instead of a
            // stale file set replacing the manifest and deleting collaborators' files.
            var baseVersion = App.State.Settings.PackSyncedVersion.TryGetValue(_pack.Id, out var v) ? v : 0;
            var newVersion = await App.State.Packs.UploadSharedAsync(_pack.Id, baseVersion, sharedPaths, progress, cts.Token);

            // Record the authoritative version the server returned from the commit (rather than
            // a redundant second manifest fetch) so the Update button stays hidden locally.
            App.State.Settings.PackSyncedVersion[_pack.Id] = newVersion;
            App.State.Settings.Save();
            await ReloadAsync();
        }
        catch (ApiException aex) when (aex.Status == System.Net.HttpStatusCode.Conflict)
        {
            StatusLabel.Text = "This pack changed on the server since you last synced. " +
                               "Download the latest changes, then upload again.";
        }
        catch (OperationCanceledException)
        {
            // Nothing is committed until the server has the whole file set, so a cancelled upload
            // leaves the server unchanged.
            StatusLabel.Text = "Upload cancelled - nothing on the server changed.";
            ProgressHub.Clear(_packId);
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
        finally
        {
            _syncCts = null;
            cts.Dispose();
            SetSyncUiRunning(false, downloading: false);
        }
    }

    /// <summary>
    /// Runs a shared-pack download as a registered job, so its progress bar has working Pause and
    /// Stop controls and PackJobs.IsRunning sees it. Returns false when it was stopped, after rolling
    /// the files back and reporting it.
    /// </summary>
    private async Task<bool> RunSharedDownloadAsync(IProgress<string>? log)
    {
        if (_pack is null) return false;

        var job = PackJobs.Start(_packId, PackJobKind.Sync, _pack.Name);
        try
        {
            await App.State.Packs.DownloadSharedAsync(_pack.Id, log, job.Token, job);
            return true;
        }
        catch (OperationCanceledException)
        {
            var removed = job.RollbackCreatedFiles();
            StatusLabel.Text = removed > 0
                ? $"Update stopped - the {removed} file(s) it had downloaded were removed."
                : "Update stopped.";
            ProgressHub.Clear(_packId);
            return false;
        }
        finally { PackJobs.Finish(job); }
    }

    /// <summary>Stops whichever transfer is running. Safe to call when none is.</summary>
    private void CancelSync(string message)
    {
        if (_syncCts is null) return;
        StatusLabel.Text = message;
        try { _syncCts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
    }

    /// <summary>
    /// Returns relative paths of all files in <paramref name="gameDir"/> that should be uploaded:
    /// files matching "shared" rules, plus any .cloudlauncher/ assets (always included).
    /// </summary>
    private static List<string> CollectSharedPaths(string gameDir, List<PackRule> rules)
    {
        var allFiles = Directory.Exists(gameDir)
            ? Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(gameDir, f).Replace('\\', '/'))
                .OrderBy(s => s, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

        var result = new List<string>();
        foreach (var rel in allFiles)
        {
            // Always include pack assets
            if (rel.StartsWith(".cloudlauncher/", StringComparison.OrdinalIgnoreCase))
            { result.Add(rel); continue; }

            var match = App.State.Rules.Match(rel, rules);
            if (match.IsAutoShared && !PrivateAssetPolicy.IsPrivate(rel, App.State.Settings))
                result.Add(rel);
        }
        return result;
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;

        // A running transfer is controlled by Pause and Stop; a second press here would only start
        // another download on top of the first.
        if (PackJobs.IsRunning(_packId) || _syncCts is not null)
        {
            StatusLabel.Text = PackJobs.For(_packId) is { } running
                ? running.Kind == PackJobKind.Export
                    ? "An export is reading this instance. Update it once that has finished."
                    : $"Already {running.KindLabel}ing - use Pause or Stop to interrupt it."
                : "A transfer is already running for this instance.";
            return;
        }

        LogBox.Text = "";
        var progress = new Progress<string>(line => AppendCapped(LogBox, line + Environment.NewLine));
        var job = PackJobs.Start(_packId, PackJobKind.Sync, _pack.Name);
        SetSyncUiRunning(true, downloading: true);
        try
        {
            await App.State.Packs.DownloadSharedAsync(_pack.Id, progress, job.Token, job);
            // PackSyncedVersion is updated inside DownloadSharedAsync
            ApplyHeroIcon(_pack);
            ApplyDescriptionDisplay(_pack.Id, _pack.Description ?? "");
            UpdateStatsLabel(_pack.Id);
            UpdateButton.Visibility = Visibility.Collapsed;
            _ = RefreshFileListsAsync();
            StatusLabel.Text = "Up to date.";
        }
        catch (OperationCanceledException)
        {
            // Stopping removes the files this run created. Files it replaced keep their new copy,
            // since each one is complete and hash-verified.
            var removed = job.RollbackCreatedFiles();
            StatusLabel.Text = removed > 0
                ? $"Update stopped - the {removed} file(s) it had downloaded were removed."
                : "Update stopped.";
            ProgressHub.Clear(_packId);
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
        finally
        {
            PackJobs.Finish(job);
            SetSyncUiRunning(false, downloading: true);
        }
    }

    // ── launch ───────────────────────────────────────────────────────────────

    private async void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        if (App.State.Instances.IsBusy(_packId))
        {
            StopActiveInstance();
            return;
        }

        if (App.State.MinecraftAccounts.Current is null)
        {
            StatusLabel.Text = "Set up a Minecraft account first - click the account chip in the title bar.";
            _shell.OpenMcAccount();
            return;
        }

        StatusLabel.Text = "Checking Java...";
        LaunchButton.IsEnabled = false;
        var (javaOk, javaMsg) = await LaunchService.CheckJavaAsync(_pack.MinecraftVersion, App.State.Settings.GetJavaPathFor(_pack.Id));
        UpdateLaunchButtonState();
        if (!javaOk)
        {
            StatusLabel.Text = javaMsg;
            await AppDialog.MessageAsync(ListOwnerWindow, "Java check", javaMsg);
            return;
        }
        LogBox.Text = "";
        IProgress<string> log = new Progress<string>(line => AppendCapped(LogBox, line + Environment.NewLine));
        MinecraftLaunchHandle? launch = null;
        try
        {
            launch = App.State.Instances.BeginLaunch(_pack.Id);
            UpdateLaunchButtonState();

            // Auto-update before launch if enabled
            if (_pack.IsShared && AutoUpdateBox.IsChecked == true
                && _pack.EffectivePermissions.HasFlag(PackPermissions.Download))
            {
                log.Report("Auto-update: pulling latest from server...");
                try
                {
                    await App.State.Packs.DownloadSharedAsync(_pack.Id, log, launch.Token);
                    UpdateButton.Visibility = Visibility.Collapsed;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log.Report("Auto-update failed: " + ex.Message); }
            }

            var proc = await App.State.Launcher.LaunchAsync(_pack, log, launch.Token);
            if (!launch.Complete(proc))
            {
                StatusLabel.Text = "Launch cancelled.";
                LogBox.AppendText("Launch cancelled; stopped Minecraft." + Environment.NewLine);
                return;
            }

            LogBox.AppendText($"PID {proc.Id} - running.{Environment.NewLine}");
            _shell.OpenMinecraftHost(_pack, proc);
            UpdateStatsLabel(_pack.Id);
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Launch cancelled.";
            LogBox.AppendText("Launch cancelled." + Environment.NewLine);
            ProgressHub.Clear(_pack.Id);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Launch failed: " + ex.Message;
            LogBox.AppendText(ex + Environment.NewLine);
        }
        finally
        {
            launch?.Dispose();
            UpdateLaunchButtonState();
        }
    }

    private void StopActiveInstance()
    {
        try
        {
            App.State.Instances.Stop(_packId);
            StatusLabel.Text = "Stopping Minecraft instance...";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Stop failed: " + ex.Message;
        }
        finally
        {
            UpdateLaunchButtonState();
        }
    }


    private void UpdateStatsLabel(Guid packId)
    {
        var stats = App.State.Settings.GetPackUsage(packId);
        var played = stats.PlayCount == 1 ? "Played 1 time" : $"Played {stats.PlayCount} times";
        var total = FormatPlayTime(TimeSpan.FromSeconds(stats.TotalPlayTimeSeconds));
        var last = stats.LastPlayedAt is { } lastPlayed
            ? $"last played {lastPlayed.LocalDateTime:g}"
            : "never played";
        PackStatsLabel.Text = $"{played} · {total} total · {last}";
    }

    private static string FormatPlayTime(TimeSpan value)
    {
        if (value.TotalMinutes < 1) return "0 min";
        if (value.TotalHours < 1) return $"{(int)value.TotalMinutes} min";
        if (value.TotalDays < 1) return $"{(int)value.TotalHours}h {value.Minutes}m";
        return $"{(int)value.TotalDays}d {value.Hours}h";
    }
}

public sealed record TagVm(string Text, Brush Background, Brush Foreground);

/// <summary>One screenshot card on the instance's Screenshots tab.</summary>
/// <remarks>The card shows a down-sampled thumbnail rather than the file itself, which WPF would
/// decode at full resolution. <see cref="FullImageUrl"/> keeps the original for full-size
/// viewing.</remarks>
public sealed class PackScreenshotRow : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Width the card decodes at, equal to the card's display width.</summary>
    public const int ThumbnailWidth = 230;

    public PackScreenshotRow(FileInfo file, string sourceLabel, bool canSetAsIcon)
    {
        Source = file;
        SourceLabel = sourceLabel;
        CanSetAsIcon = canSetAsIcon;
        ImageUrl = new Uri(file.FullName).AbsoluteUri;
        Title = Path.GetFileNameWithoutExtension(file.Name);
        MetaLabel = $"{sourceLabel} · {file.LastWriteTime:g} · {FormatSize(file.Length)}";
        ToolTipText = $"{file.FullName}\n{MetaLabel}\nClick to open · right-click for more";
    }

    public FileInfo Source { get; }
    public string SourceLabel { get; }
    public string ImageUrl { get; }
    public string FullImageUrl => ImageUrl;
    public string Title { get; }
    public string MetaLabel { get; }
    public string ToolTipText { get; }
    public DateTime LastWriteTime => Source.LastWriteTime;

    /// <summary>False on an instance someone else owns, whose cover art we cannot change.</summary>
    public bool CanSetAsIcon { get; }

    private BitmapSource? _thumbnail;

    /// <summary>The card's picture. Null until the background pass has decoded it.</summary>
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    /// <summary>Hands the row a bitmap decoded elsewhere. It must be frozen: it is built on a
    /// background thread and read by the UI one.</summary>
    public void SetThumbnail(BitmapSource? image) => Thumbnail = image;

    /// <summary>Decodes a card-sized, frozen copy of a screenshot. Safe to call off the UI thread,
    /// and returns null rather than throwing on a file that is half-written or not an image.</summary>
    public static BitmapSource? DecodeThumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = ThumbnailWidth;
            // OnLoad closes the file handle before returning; otherwise deleting or renaming the
            // screenshot from the card's menu fails with a sharing violation.
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
        : $"{bytes} B";
}

/// <summary>What a row in the Logs list stands for.</summary>
/// <remarks>Drives the row's icon colours through template triggers. Brushes are not stored on the
/// row, since they would not follow later theme changes.</remarks>
public enum LogRowKind
{
    /// <summary>A Minecraft log file from game/logs.</summary>
    GameLog,

    /// <summary>A crash report from game/crash-reports.</summary>
    CrashReport,

    /// <summary>The in-memory upload/download log for this session.</summary>
    Sync,

    /// <summary>The launcher's own activity log.</summary>
    Launcher
}

public sealed class LogRow
{
    public string Key { get; private set; } = "";
    public string Path { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public string MetaLabel { get; private set; } = "";
    public string ToolTipText { get; private set; } = "";
    public LogRowKind Kind { get; private set; } = LogRowKind.GameLog;
    public bool IsSyncLog => Kind == LogRowKind.Sync;
    public bool IsLauncherLog => Kind == LogRowKind.Launcher;

    /// <summary>True when a real file sits behind this row, so it can be revealed, copied or deleted.
    /// The two pseudo-rows are live buffers with nothing on disk.</summary>
    public bool HasFile => Path.Length > 0;

    public string IconGlyph { get; private set; } = "";

    /// <summary>Last-write time of the file behind the row; <see cref="DateTime.MinValue"/> for the
    /// pseudo-rows, which are pinned to the top of the list anyway.</summary>
    public DateTime SortTime { get; private set; } = DateTime.MinValue;

    /// <summary>Segoe MDL2 glyphs are private-use code points, so they are written as numbers rather
    /// than as characters editors and diffs cannot show.</summary>
    private static string Glyph(int codePoint) => ((char)codePoint).ToString();

    /// <summary>Parameterless ctor used by the pseudo-row factories.</summary>
    private LogRow() { }

    public LogRow(System.IO.FileInfo f, bool isCrashReport = false)
    {
        Key = "file:" + f.FullName;
        Path = f.FullName;
        DisplayName = f.Name;
        var size = f.Length switch
        {
            >= 1024L * 1024 => $"{f.Length / (1024.0 * 1024):F1} MB",
            >= 1024 => $"{f.Length / 1024.0:F0} KB",
            _ => $"{f.Length} B"
        };
        MetaLabel = (isCrashReport ? "Crash report · " : "") + $"{f.LastWriteTime:g} · {size}";
        Kind = isCrashReport ? LogRowKind.CrashReport : LogRowKind.GameLog;
        SortTime = f.LastWriteTime;
        IconGlyph = isCrashReport ? Glyph(0xE7BA) : Glyph(0xE7C3); // warning triangle / document
        ToolTipText = f.FullName;
    }

    /// <summary>Pseudo-entry for the global launcher activity log.</summary>
    public static LogRow LauncherLogPseudo() => new()
    {
        Key = "launcher",
        Path = "",
        DisplayName = "Launcher log",
        MetaLabel = "What the launcher is doing right now",
        Kind = LogRowKind.Launcher,
        IconGlyph = Glyph(0xE9D9), // Diagnostic / activity glyph
        ToolTipText = "The launcher's own log for this session - not a file on disk."
    };

    /// <summary>Returns the pseudo-entry that shows the in-memory sync log content.</summary>
    public static LogRow SyncLogPseudo() => new()
    {
        Key = "sync",
        Path = "",
        DisplayName = "Sync log",
        MetaLabel = "Upload / download activity from this session",
        Kind = LogRowKind.Sync,
        IconGlyph = Glyph(0xE753),   // Cloud glyph
        ToolTipText = "Upload and download output from this session - not a file on disk."
    };
}
