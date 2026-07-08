using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

public partial class PackDetailView : Page
{
    private readonly MainWindow _shell;
    private readonly Guid _packId;
    private readonly bool _hostedInMinecraftWindow;

    /// <summary>The instance this page is showing. Used by the shell to close the page if the instance is deleted.</summary>
    public Guid PackId => _packId;

    private PackDetail? _pack;
    private bool _isOwner;
    private bool _suppressEvents;
    private bool _fileTransferInProgress;

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

    public event EventHandler<double>? HostPanelWidthRequested;

    public PackDetailView(MainWindow shell, Guid packId, bool hostedInMinecraftWindow = false)
    {
        InitializeComponent();
        _shell = shell;
        _packId = packId;
        _hostedInMinecraftWindow = hostedInMinecraftWindow;
        _autoApplyTimer.Tick += OnAutoApplyTick;
        _overviewSaveTimer.Tick += OnOverviewSaveTimerTick;
        WireOverviewTextBox(OverviewSummaryBox);
        RichDescriptionHelper.AttachHost(OverviewDescriptionHost, OverviewDescriptionBrowser);
        WorldsList.ItemsSource = _worldRows;
        PackScreenshotList.ItemsSource = _packScreenshotRows;
        GameView.FilesDropped   += OnFilesDropped;
        SharedView.FilesDropped += OnFilesDropped;
        ProgressHub.ProgressChanged += OnHeroProgressChanged;
        ProgressHub.ProgressCleared += OnHeroProgressCleared;
        App.State.Instances.StateChanged += OnInstanceStateChanged;
        App.State.ModpackDownload.PackAdded += OnPackDownloadUpdated;
        AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPagePreviewMouseWheel), true);
        Loaded += async (_, _) => await ReloadAsync();
        if (_hostedInMinecraftWindow)
        {
            DetailTabs.HorizontalAlignment = HorizontalAlignment.Left;
            DetailTabs.ItemContainerStyle = CreateHostedTabItemStyle();
            DetailTabs.SizeChanged += (_, _) => NotifyHostPanelWidth();
        }
        Unloaded += async (_, _) =>
        {
            _autoApplyTimer.Stop();
            _overviewSaveTimer.Stop();
            // Unsubscribe FIRST, before any awaitable/throwable work. Otherwise a throw in the
            // save below would skip unsubscription and leak this whole view (its visual tree,
            // WebView2/WebBrowser hosts, and the static AppLog handler) for the process lifetime.
            ProgressHub.ProgressChanged -= OnHeroProgressChanged;
            ProgressHub.ProgressCleared -= OnHeroProgressCleared;
            App.State.Instances.StateChanged -= OnInstanceStateChanged;
            App.State.ModpackDownload.PackAdded -= OnPackDownloadUpdated;
            // The Logs tab wires this static event when "Launcher log" is selected; if the panel
            // closes on that row it was never removed, rooting the view forever.
            AppLog.MessageAppended -= OnLauncherLogAppended;
            if (_pack is not null && _isOwner)
                await SaveOverviewDescriptionAsync();
        };
    }

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

    private void OnInstanceStateChanged(Guid packId)
    {
        if (packId != _packId) return;
        UpdateLaunchButtonState();
    }

    private void OnPackDownloadUpdated(PackSummary pack)
    {
        if (pack.Id != _packId) return;
        Dispatcher.BeginInvoke(async () => await ReloadAsync());
    }

    private void UpdateLaunchButtonState()
    {
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

    /// <summary>
    /// Drop handler for FolderViews. When dropping onto the SharedView (same Root as GameView,
    /// ShowOnlyShared mode), adds a "shared" rule for the dropped files instead of moving them —
    /// because the files are already in game/ and just need to be marked for sync.
    /// All other drops move/copy between folders as normal.
    /// </summary>
    private async void OnFilesDropped(string sourceRoot, string destRoot, IReadOnlyList<string> relativePaths)
    {
        if (_pack is null || _fileTransferInProgress) return;

        var gameDir = App.State.Packs.GameDir(_packId);
        var droppingOntoSyncView =
            string.Equals(destRoot, gameDir, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(sourceRoot, gameDir, StringComparison.OrdinalIgnoreCase);

        if (droppingOntoSyncView)
        {
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

        FilesTab.Visibility = Visibility.Collapsed;
        OptionsTab.Visibility = Visibility.Collapsed;

        // Inside the Minecraft host window the Overview tab is read-only:
        // force the summary box to read-only and hide editing-only chrome.
        OverviewSummaryBox.IsReadOnly = true;
        OverviewSaveStatusLabel.Visibility = Visibility.Collapsed;
        OverviewSummaryCountLabel.Visibility = Visibility.Collapsed;
        OverviewCharacterCountLabel.Visibility = Visibility.Collapsed;

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
        if (_pack is null) return;
        if (force)
            _tabsLoaded.Remove(tab);
        else if (!_tabsLoaded.Add(tab))
            return;

        switch (tab)
        {
            case DetailTabKind.Mods:
                ModListCtrl.Load(_pack, ListOwnerWindow);
                break;
            case DetailTabKind.ResourcePacks:
                ResourcePackListCtrl.Load(_pack, ListOwnerWindow);
                break;
            case DetailTabKind.Files:
                _ = RefreshFileListsAsync();
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

            ApplyPack(_pack);
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
        }
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

            StatusLabel.Text = "Syncing shared files…";
            await App.State.Packs.DownloadSharedAsync(_pack.Id, null);
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
    /// If the pack has team associations, ensure it's linked into each "team:&lt;id&gt;" folder
    /// — the user sees team-shared packs in their team folder automatically.
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

    private void RefreshLogsList()
    {
        if (_pack is null) return;
        var rows = new List<LogRow>
        {
            LogRow.LauncherLogPseudo(),
            LogRow.SyncLogPseudo()
        };
        var dir = Path.Combine(App.State.Packs.GameDir(_packId), "logs");
        if (Directory.Exists(dir))
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.log*").OrderByDescending(f => f.LastWriteTime))
                rows.Add(new LogRow(f));
        }

        var previouslySelected = (LogsList.SelectedItem as LogRow)?.Key;
        LogsList.ItemsSource = rows;
        var fileCount = rows.Count - 2;
        LogsCountLabel.Text = fileCount <= 0 ? "(no Minecraft logs yet)" : $"{fileCount} Minecraft log{(fileCount == 1 ? "" : "s")}";

        var match = previouslySelected != null
            ? rows.FirstOrDefault(r => r.Key == previouslySelected)
            : null;
        LogsList.SelectedItem = match ?? rows[0];
    }

    private void OnRefreshLogs(object sender, RoutedEventArgs e) => RefreshLogsList();

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(App.State.Packs.GameDir(_packId), "logs");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void OnLogsSelected(object sender, SelectionChangedEventArgs e)
    {
        // Unsubscribe launcher-log tailing whenever the selection changes
        AppLog.MessageAppended -= OnLauncherLogAppended;

        if (LogsList.SelectedItem is not LogRow row) { LogContent.Text = ""; return; }
        if (row.IsLauncherLog)
        {
            LogContent.Text = AppLog.Buffer;
            LogContent.ScrollToEnd();
            AppLog.MessageAppended += OnLauncherLogAppended;
            return;
        }
        if (row.IsSyncLog) { LogContent.Text = LogBox.Text; return; }
        try
        {
            string text;
            if (row.Path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(row.Path);
                using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
                using var sr = new StreamReader(gz);
                text = sr.ReadToEnd();
            }
            else
            {
                using var fs = new FileStream(row.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                text = sr.ReadToEnd();
            }
            const int maxChars = 200_000;
            if (text.Length > maxChars) text = "[…older lines trimmed…]\n" + text.Substring(text.Length - maxChars);
            LogContent.Text = text;
        }
        catch (Exception ex) { LogContent.Text = "Could not read log: " + ex.Message; }
    }

    /// <summary>Mirror the hidden LogBox into the Logs tab if the user is viewing the sync log.</summary>
    private void OnLogBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (LogsList.SelectedItem is LogRow row && row.IsSyncLog)
            LogContent.Text = LogBox.Text;
    }

    /// <summary>Live-append a launcher-log line to the content view (only when that entry is selected).</summary>
    private void OnLauncherLogAppended(string line)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnLauncherLogAppended(line)); return; }
        AppendCapped(LogContent, line + Environment.NewLine);
        LogContent.ScrollToEnd();
    }

    /// <summary>Max chars kept in the in-memory log TextBoxes. WPF TextBox layout/append cost grows
    /// with content and Minecraft streams its whole session through these, so cap the retained text
    /// to keep appends bounded and stop the progressive lag. Matches the on-disk log-viewer cap.</summary>
    private const int MaxLogChars = 200_000;

    /// <summary>Append text to a log TextBox, trimming the oldest content once it exceeds the cap.
    /// Drops a chunk at a time so we don't re-trim on every single append.</summary>
    private static void AppendCapped(TextBox box, string text)
    {
        box.AppendText(text);
        if (box.Text.Length <= MaxLogChars) return;
        var kept = box.Text[^(MaxLogChars * 9 / 10)..];
        box.Text = "[…older lines trimmed…]" + Environment.NewLine + kept;
        box.CaretIndex = box.Text.Length;
    }

    private async void RefreshWorlds()
    {
        if (_pack is null) return;
        _worldRows.Clear();
        WorldCountLabel.Text = "Scanning saves…";
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
        catch { /* offline or no api — skip */ }
    }

    private void RefreshPackScreenshots()
    {
        _packScreenshotRows.Clear();

        var rows = new List<PackScreenshotRow>();
        foreach (var (dir, label) in PackScreenshotFolders())
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                var directory = new DirectoryInfo(dir);
                foreach (var file in directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    if (IsScreenshotFile(file))
                        rows.Add(new PackScreenshotRow(file, label));
                }
            }
            catch (Exception ex)
            {
                StatusLabel.Text = "Could not read screenshots: " + ex.Message;
            }
        }

        foreach (var row in rows.OrderByDescending(r => r.LastWriteTime))
            _packScreenshotRows.Add(row);

        PackScreenshotList.Visibility = _packScreenshotRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PackScreenshotsEmptyText.Visibility = _packScreenshotRows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        PackScreenshotCountLabel.Text = _packScreenshotRows.Count switch
        {
            0 => "No screenshots found in this instance.",
            1 => "1 screenshot found in this instance.",
            var count => $"{count} screenshots found in this instance."
        };
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
        if (sender is FrameworkElement { DataContext: PackScreenshotRow row })
        {
            e.Handled = true;
            ScreenshotPreviewWindow.ShowFor(Window.GetWindow(this), row.FullImageUrl, row.Title);
        }
    }

    private void OnWorldOpenFolder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string path)
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
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

            // File routing is only useful once the pack is hosted on the server.
            FilesTab.Visibility   = _isOwner && pack.IsShared ? Visibility.Visible : Visibility.Collapsed;
            SharingOptionsPanel.Visibility = _isOwner ? Visibility.Visible : Visibility.Collapsed;
            ApplyHostedTabChrome();

            // RAM + auto-update
            var ram = App.State.Settings.GetMaxRamFor(pack.Id);
            RamSlider.Value = ram;
            RamValueLabel.Text = $"{ram} MB";
            AutoUpdateBox.IsChecked = App.State.Settings.PackAutoUpdate.TryGetValue(pack.Id, out var u) && u;
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

            // Folder buttons availability
            var hasShared = pack.IsShared;
            OpenLocalButton.IsEnabled = hasShared;
            OpenSharedButton.IsEnabled = hasShared;
            UploadButton.IsEnabled = hasShared && pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared);

            // Launch buttons
            var playable = !pack.IsEmpty && !string.IsNullOrEmpty(pack.MinecraftVersion);
            UpdateLaunchButtonState();
            StartServerButton.IsEnabled = playable;
            OpenServerButton.IsEnabled = playable;

            // Update button is hidden until CheckForUpdateAsync confirms one is available
            UpdateButton.Visibility = Visibility.Collapsed;

            // Auto-apply rules — default ON, persisted per pack.
            // Only relevant when the pack is shared (so local/shared destinations exist) and the user can edit.
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

    /// <summary>Show the cover-edit affordance for owners and gate "Remove image" on there being one.</summary>
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

    /// <summary>Re-read the cover from disk after it was changed elsewhere (e.g. the pack list).</summary>
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
        if (DescriptionEditorHelper.TryGetEditorHtml(OverviewDescriptionBrowser, out var html))
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
        DescriptionEditorHelper.ShowEditor(
            OverviewDescriptionBrowser,
            content,
            isMarkdown,
            OnDescriptionEditorChanged);
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
        DescriptionEditorHelper.ShowViewer(
            OverviewDescriptionBrowser,
            content,
            isMarkdown,
            CreateDescriptionOptions());
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
            OverviewDescriptionBrowser.Visibility = Visibility.Collapsed;
            return;
        }

        var onOverview = _currentTab == DetailTabKind.Overview;
        OverviewDescriptionHost.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
        OverviewDescriptionBrowser.Visibility = onOverview ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPagePreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_showRichDescription || _currentTab != DetailTabKind.Overview) return;
        if (OverviewDescriptionHost.Visibility != Visibility.Visible) return;
        if (OverviewDescriptionHost.ActualWidth <= 0 || OverviewDescriptionHost.ActualHeight <= 0) return;

        var pos = e.GetPosition(OverviewDescriptionHost);
        if (pos.X < 0 || pos.Y < 0 || pos.X > OverviewDescriptionHost.ActualWidth || pos.Y > OverviewDescriptionHost.ActualHeight)
            return;

        if (RichDescriptionHelper.TryScroll(OverviewDescriptionBrowser, e.Delta))
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

    private void OpenOverviewLink(string link)
    {
        try
        {
            var target = link;
            if (target.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                target = "https://" + target;
            else if (!target.Contains(":", StringComparison.Ordinal)
                     && target.Contains('@', StringComparison.Ordinal))
                target = "mailto:" + target;

            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Could not open link: " + ex.Message;
        }
    }

    /// <summary>
    /// Reflect the auto-apply state into the timer and the manual Apply rules button.
    /// — When on: hide the manual button (it's redundant) and start the safety-net timer.
    /// — When off: stop the timer and show the manual button.
    /// </summary>
    private void UpdateAutoApplyUi()
    {
        var auto = AutoApplyBox.IsChecked == true && _pack?.IsShared == true;
        ApplyRulesButton.Visibility = auto ? Visibility.Collapsed : Visibility.Visible;
        if (auto && !_autoApplyTimer.IsEnabled) _autoApplyTimer.Start();
        else if (!auto && _autoApplyTimer.IsEnabled) _autoApplyTimer.Stop();
        AutoApplyLabel.Text = auto ? "On — rules apply automatically." : "";
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
    /// Check whether the server has a newer shared-instance version than what's locally synced.
    /// Reveal the Update button only if so. If the pack has never been downloaded (locallySynced == 0),
    /// trigger the initial download automatically so the user doesn't have to click it.
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
                StatusLabel.Text = "Downloading server content…";
                await App.State.Packs.DownloadSharedAsync(_pack.Id, null);
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
        catch { /* offline or auth issues — don't show anything */ }
    }

    // ── file lists ───────────────────────────────────────────────────────────

    private void RefreshFileLists() => _ = RefreshFileListsAsync();

    private async Task RefreshFileListsAsync(bool runAutoApply = true)
    {
        if (_pack is null) return;

        var p = App.State.Packs;
        var packRoot = p.PackRoot(_pack.Id);
        var rules = App.State.Rules.Load(packRoot);
        var gameDir = p.GameDir(_pack.Id);
        Directory.CreateDirectory(gameDir);

        // Left: game/ view with rule badges. Shared files live in game/ on disk (the live instance
        // needs them at launch) but are hidden here — they're surfaced in the Shared column instead.
        GameView.Root  = gameDir;
        GameView.Rules = rules;
        GameView.MirrorLocalRoot  = null;
        GameView.MirrorSharedRoot = null;
        GameView.HideShared = _pack.IsShared;

        // Right: same game/ directory but filtered to only "shared"-rule files — the sync preview
        // (what will be uploaded). Always populated, even before sharing is enabled, so marking a
        // file shared has an immediate, visible effect; the upload itself still requires sharing.
        SharedView.Root           = gameDir;
        SharedView.Rules          = rules;
        SharedView.ShowOnlyShared = true;
        SharedView.MirrorLocalRoot  = null;
        SharedView.MirrorSharedRoot = null;

        // Both views scan the same game/ tree independently — run them concurrently so the Files
        // tab doesn't wait for one full scan before starting the other.
        await Task.WhenAll(GameView.RefreshAsync(), SharedView.RefreshAsync());
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
        if (_pack is null) return;
        var dlg = new PackRulesDialog(
            App.State.Packs.PackRoot(_packId),
            _pack.Name,
            packId: _pack.IsShared ? _packId : null,
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
            StatusLabel.Text = "Enable server hosting first.";
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
            StatusLabel.Text = "Enable server hosting first.";
            return;
        }
        var (_, message) = await ApplyRulesCoreAsync(silent: false);
        StatusLabel.Text = message;
    }

    /// <summary>
    /// Scans game/ against the current rules and refreshes the sync-preview pane (SharedView).
    /// Files are not moved — rules determine which game/ files get uploaded on the next sync.
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
            // A folder needs a trailing-slash pattern so the Local rule covers its CONTENTS.
            // Inserting it at the front (highest precedence, first-match-wins) makes it override a
            // broader parent rule like "mods/" → shared, so only this sub-folder becomes local.
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
            // A folder's rule must end in '/' so the matcher expands it to '/**' and covers the
            // folder's CONTENTS. Without the slash it's an exact match on the folder name only, so
            // nothing inside it is shared — which looks like "marking a folder shared does nothing".
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
        OpenInExplorer(App.State.Packs.GameDir(_packId)); // shared/ removed; game/ is the sync source
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
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private static void OpenInExplorer(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    // ── moves ────────────────────────────────────────────────────────────────

    // "Copy to shared" → adds a "shared" rule so the file is included in the next upload.
    private void OnMoveGameToShared(object sender, RoutedEventArgs e) =>
        AddQuickRule(RuleAction.Shared, folderMode: false);

    // "Remove from shared" → marks the file ignored/local so it is excluded from uploads.
    private void OnMoveSharedToGame(object sender, RoutedEventArgs e) =>
        RemoveFromShared(SharedView.GetSelectedEntries().Select(e => e.RelativePath).ToList());

    private async Task TransferEntriesAsync(string sourceRoot, string destRoot, IReadOnlyList<string> relativePaths)
    {
        if (_pack is null || _fileTransferInProgress) return;

        _fileTransferInProgress = true;
        StatusLabel.Text = "Moving files…";
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
                var srcAbs = Path.Combine(sourceRoot, rel.Replace('/', Path.DirectorySeparatorChar));
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
                var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
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

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        LogBox.Text = "";
        var progress = new Progress<string>(line => AppendCapped(LogBox, line + Environment.NewLine));
        try
        {
            UploadButton.IsEnabled = false;
            if (_isOwner)
                await SaveOverviewDescriptionAsync();

            // Ensure pack assets (icon, description) are staged in game/.cloudlauncher/
            App.State.PackAssets.MirrorToSharedFolder(_pack.Id);

            var manifest = await App.State.Api.GetManifestAsync(_pack.Id);

            // Determine which files in game/ to include: those matching "shared" rules,
            // plus any .cloudlauncher/ assets.
            var packRoot = App.State.Packs.PackRoot(_pack.Id);
            var rules = App.State.Rules.Load(packRoot);
            var gameDir = App.State.Packs.GameDir(_pack.Id);
            var sharedPaths = await Task.Run(() => CollectSharedPaths(gameDir, rules));

            var newVersion = await App.State.Packs.UploadSharedAsync(_pack.Id, manifest.Version, sharedPaths, progress);

            // Record the authoritative version the server returned from the commit (rather than
            // a redundant second manifest fetch) so the Update button stays hidden locally.
            App.State.Settings.PackSyncedVersion[_pack.Id] = newVersion;
            App.State.Settings.Save();
            await ReloadAsync();
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
        finally { UploadButton.IsEnabled = true; }
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
            if (match.IsAutoShared)
                result.Add(rel);
        }
        return result;
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        LogBox.Text = "";
        var progress = new Progress<string>(line => AppendCapped(LogBox, line + Environment.NewLine));
        try
        {
            UploadButton.IsEnabled = false;
            await App.State.Packs.DownloadSharedAsync(_pack.Id, progress);
            // PackSyncedVersion is updated inside DownloadSharedAsync
            ApplyHeroIcon(_pack);
            ApplyDescriptionDisplay(_pack.Id, _pack.Description ?? "");
            UpdateStatsLabel(_pack.Id);
            UpdateButton.Visibility = Visibility.Collapsed;
            _ = RefreshFileListsAsync();
            StatusLabel.Text = "Up to date.";
        }
        catch (Exception ex) { StatusLabel.Text = ex.Message; }
        finally { UploadButton.IsEnabled = true; }
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
            StatusLabel.Text = "Set up a Minecraft account first — click the account chip in the title bar.";
            _shell.OpenMcAccount();
            return;
        }

        StatusLabel.Text = "Checking Java...";
        LaunchButton.IsEnabled = false;
        var (javaOk, javaMsg) = await LaunchService.CheckJavaAsync(_pack.MinecraftVersion);
        UpdateLaunchButtonState();
        if (!javaOk)
        {
            StatusLabel.Text = javaMsg;
            MessageBox.Show(_shell, javaMsg, "Java check",
                MessageBoxButton.OK, MessageBoxImage.Warning);
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
                log.Report("Auto-update: pulling latest from server…");
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

            LogBox.AppendText($"PID {proc.Id} — running.{Environment.NewLine}");
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

    private async void OnStartServer(object sender, RoutedEventArgs e)
    {
        if (_pack is null) return;
        LogBox.Text = "";
        var log = new Progress<string>(line => AppendCapped(LogBox, line + Environment.NewLine));
        StartServerButton.IsEnabled = false;
        try
        {
            var proc = await App.State.Launcher.StartLocalServerAsync(_pack, log);
            LogBox.AppendText($"Server started, PID {proc.Id}. A console window should appear.{Environment.NewLine}");
            LogBox.AppendText("Players connect on port 25565." + Environment.NewLine);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Server start failed: " + ex.Message;
            LogBox.AppendText(ex + Environment.NewLine);
        }
        finally { StartServerButton.IsEnabled = !_pack.IsEmpty; }
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

public sealed class PackScreenshotRow
{
    public PackScreenshotRow(FileInfo file, string sourceLabel)
    {
        Source = file;
        SourceLabel = sourceLabel;
        ImageUrl = new Uri(file.FullName).AbsoluteUri;
        Title = Path.GetFileNameWithoutExtension(file.Name);
        MetaLabel = $"{sourceLabel} · {file.LastWriteTime:g} · {FormatSize(file.Length)}";
    }

    public FileInfo Source { get; }
    public string SourceLabel { get; }
    public string ImageUrl { get; }
    public string FullImageUrl => ImageUrl;
    public string Title { get; }
    public string MetaLabel { get; }
    public DateTime LastWriteTime => Source.LastWriteTime;

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 ? $"{bytes / (1024.0 * 1024):F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:F0} KB"
        : $"{bytes} B";
}

public sealed class LogRow
{
    public string Key { get; private set; } = "";
    public string Path { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public string MetaLabel { get; private set; } = "";
    public bool IsSyncLog { get; private set; }
    public bool IsLauncherLog { get; private set; }
    public string IconGlyph { get; private set; } = "";
    public Brush IconBackground { get; private set; } = (Brush)Application.Current.FindResource("Surface3Brush");
    public Brush IconForeground { get; private set; } = (Brush)Application.Current.FindResource("TextSecondaryBrush");

    /// <summary>Parameterless ctor used by SyncLogPseudo().</summary>
    private LogRow() { }

    public LogRow(System.IO.FileInfo f)
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
        MetaLabel = $"{f.LastWriteTime:g} · {size}";
        IsSyncLog = false;
        IconGlyph = ""; // Document glyph
    }

    /// <summary>Pseudo-entry for the global launcher activity log.</summary>
    public static LogRow LauncherLogPseudo() => new()
    {
        Key = "launcher",
        Path = "",
        DisplayName = "Launcher log",
        MetaLabel = "What the launcher is doing right now",
        IsLauncherLog = true,
        IconGlyph = "", // Diagnostic / activity glyph
        IconBackground = (Brush)Application.Current.FindResource("Surface4Brush"),
        IconForeground = (Brush)Application.Current.FindResource("WarningBrush"),
    };

    /// <summary>Returns the pseudo-entry that shows the in-memory sync log content.</summary>
    public static LogRow SyncLogPseudo() => new()
    {
        Key = "sync",
        Path = "",
        DisplayName = "Sync log",
        MetaLabel = "Upload / download activity from this session",
        IsSyncLog = true,
        IconGlyph = "",   // Cloud glyph
        IconBackground = (Brush)Application.Current.FindResource("AccentSoftBrush"),
        IconForeground = (Brush)Application.Current.FindResource("AccentBrush"),
    };
}
