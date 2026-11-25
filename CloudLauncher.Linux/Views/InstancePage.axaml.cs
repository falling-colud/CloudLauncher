using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>A row of the instance page's Log tab: this session's output, the launcher's log, or a log
/// file of the game's.</summary>
public sealed record InstanceLogSource(string Key, string Name, string Meta, string Glyph, string? Path, bool IsCrash);

/// <summary>
/// One instance: its header with Play (Stop while the game runs), the sync actions of a shared
/// instance, and the Mods, Options and Log tabs. The Windows launcher's PackDetailView, with its calls,
/// checks and wording, without what is Windows-only (the game window the launcher hosts, the
/// ReLauncher, test runs) and without the tabs other pages of this launcher cover.
/// </summary>
public partial class InstancePage : UserControl, IShellPage, IRefreshable
{
    /// <summary>The log and JVM-argument font: whichever monospace face the system has.</summary>
    public static readonly FontFamily MonoFont =
        new("Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, Noto Sans Mono, monospace");

    /// <summary>The Java installations the Options tab offers. Finding them runs <c>java -version</c>
    /// for each; the shots harness swaps in a fixed list so it starts no Java.</summary>
    public static Func<Task<IReadOnlyList<LaunchService.JavaInstall>>> JavaInstallsSource { get; set; } =
        () => LaunchService.DetectJavaInstallsAsync();

    private const int TabMods = 0, TabOptions = 1, TabLog = 2;

    private readonly Guid _packId;
    private readonly string _name;
    private readonly Panel _heroCover;
    private readonly Image _heroImage;
    private readonly TextBlock _heroLetter;

    private PackDetail? _pack;
    private bool _isOwner;
    /// <summary>The record the page last drew, as JSON: an answer that is the same draws nothing.</summary>
    private string? _appliedSnapshot;
    private PackDetail? _modsFor;

    /// <summary>The server has newer shared files than this computer (<see cref="CheckForUpdateAsync"/>).</summary>
    private bool _updateAvailable;
    /// <summary>Never downloaded here: the update button offers Download, and nothing starts by itself.</summary>
    private bool _neverDownloaded;
    /// <summary>Set while an upload runs, so the same button cancels it.</summary>
    private CancellationTokenSource? _uploadCts;
    /// <summary>Between Play and the game starting (the account, manual-download and Java checks).</summary>
    private bool _launching;

    private bool _attached;
    private bool _reloading;
    private bool _suppress;
    private bool _javaLoading;
    private bool _logsListed;
    private DispatcherTimer? _ramSave;
    private readonly DispatcherTimer _logFlush = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _logDirty;
    private InstanceLogSource? _logShown;
    private int _logRead;

    /// <summary>For the XAML previewer; the launcher always opens a page on an instance.</summary>
    public InstancePage() : this(Guid.Empty, "Instance") { }

    public InstancePage(Guid packId, string name)
    {
        _packId = packId;
        _name = name;
        InitializeComponent();

        _heroCover = InstanceArt.Cover(packId, name, 46, out _heroImage, out _heroLetter);
        IconBox.Child = _heroCover;
        NameText.Text = name;
        ModsHost.Content = Placeholder("Loading this instance...");

        Tabs.SelectionChanged += OnTabChanged;
        LogList.SelectionChanged += OnLogSelected;
        RamSlider.PropertyChanged += OnRamSliderChanged;
        JvmArgsBox.LostFocus += (_, _) => SaveJvmArgs();
        JvmArgsBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) SaveJvmArgs();
        };
        JavaBox.SelectionChanged += OnJavaChanged;
        AutoUpdateBox.IsCheckedChanged += OnAutoUpdateToggled;
        _logFlush.Tick += (_, _) => FlushLog();
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();

        PaintRemembered();
        UpdateLaunchState();
    }

    public ShellNav Nav => ShellNav.Instances;

    public Guid PackId => _packId;

    public void OnShown()
    {
        UpdateLaunchState();
        UpdateStats();
        _ = UpdateManualDownloadsAsync();
        _ = ReloadAsync();
    }

    /// <summary>F5: the record again, and the tab on show.</summary>
    public async Task RefreshAsync()
    {
        await ReloadAsync();
        switch (SelectedTab)
        {
            case TabMods: LoadModsTab(force: true); break;
            case TabLog: RefreshLogList(); break;
        }
    }

    private void OnBack(object? sender, RoutedEventArgs e) => MainWindow.Current.GoBack();

    // ── service events ───────────────────────────────────────────────────────

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        ProgressHub.ProgressChanged += OnProgressChanged;
        ProgressHub.ProgressCleared += OnProgressCleared;
        PackJobs.Changed += OnPackJobChanged;
        App.State.Instances.StateChanged += OnInstanceStateChanged;
        App.State.ModpackDownload.PackAdded += OnPackUpdated;
        App.State.ManualDownloads.Changed += OnManualDownloadsChanged;
        InstanceLog.Appended += OnInstanceLogAppended;
        InstanceLog.Cleared += OnInstanceLogCleared;
        if (_logShown?.Key == "launcher") AppLog.MessageAppended += OnLauncherLogLine;
        _logFlush.Start();
        // Progress heard before the page went away may have ended unheard; one still running reports
        // again within moments. The live log may have grown meanwhile.
        OnProgressCleared(_packId);
        _logDirty = _logShown is not null && _logShown.Key is "game" or "launcher";
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        // Unsubscribe first: these are static events and would keep the page alive.
        ProgressHub.ProgressChanged -= OnProgressChanged;
        ProgressHub.ProgressCleared -= OnProgressCleared;
        PackJobs.Changed -= OnPackJobChanged;
        App.State.Instances.StateChanged -= OnInstanceStateChanged;
        App.State.ModpackDownload.PackAdded -= OnPackUpdated;
        App.State.ManualDownloads.Changed -= OnManualDownloadsChanged;
        InstanceLog.Appended -= OnInstanceLogAppended;
        InstanceLog.Cleared -= OnInstanceLogCleared;
        AppLog.MessageAppended -= OnLauncherLogLine;
        _logFlush.Stop();
        // What was typed or dragged still counts.
        if (_ramSave?.IsEnabled == true) SaveRam();
        SaveJvmArgs();
        // An upload reports into this page, as on Windows: leaving stops it (nothing is committed until
        // it finishes). Downloads are jobs of the instance and carry on.
        try { _uploadCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void OnInstanceStateChanged(Guid packId)
    {
        if (packId != _packId) return;
        UpdateLaunchState();
        UpdateStats();
    }

    private void OnPackJobChanged(Guid packId)
    {
        if (packId == _packId) UpdateLaunchState();
    }

    private void OnPackUpdated(PackSummary pack)
    {
        if (pack.Id == _packId) _ = ReloadAsync();
    }

    private void OnManualDownloadsChanged(Guid packId)
    {
        if (packId == _packId) _ = UpdateManualDownloadsAsync();
    }

    private void OnProgressChanged(ProgressInfo info)
    {
        if (info.PackId != _packId) return;
        ProgressArea.IsVisible = true;
        ProgressLabel.Text = info.Label;
        if (info.Fraction < 0)
        {
            MainProgress.IsIndeterminate = true;
            ProgressPercent.Text = "";
        }
        else
        {
            MainProgress.IsIndeterminate = false;
            MainProgress.Value = info.Fraction * 100;
            ProgressPercent.Text = $"{(int)(info.Fraction * 100)}%";
        }

        CurrentHeader.IsVisible = info.HasCurrentProgress;
        CurrentProgress.IsVisible = info.HasCurrentProgress;
        if (!info.HasCurrentProgress) return;
        CurrentLabel.Text = string.IsNullOrWhiteSpace(info.CurrentLabel) ? "Current stage" : info.CurrentLabel;
        CurrentProgress.IsIndeterminate = info.CurrentFraction < 0;
        CurrentProgress.Value = info.CurrentFraction < 0 ? 0 : info.CurrentFraction * 100;
        CurrentPercent.Text = info.CurrentFraction < 0 ? "" : $"{(int)(info.CurrentFraction * 100)}%";
    }

    private void OnProgressCleared(Guid packId)
    {
        if (packId != _packId) return;
        ProgressArea.IsVisible = false;
        MainProgress.IsIndeterminate = false;
        MainProgress.Value = 0;
        CurrentProgress.IsIndeterminate = false;
        ProgressLabel.Text = ProgressPercent.Text = CurrentLabel.Text = CurrentPercent.Text = "";
    }

    // ── the record ───────────────────────────────────────────────────────────

    /// <summary>Draws what this computer last knew about the instance, so the page doesn't open empty
    /// and jump when the server answers.</summary>
    private void PaintRemembered()
    {
        PackDetail? remembered = null;
        try { remembered = App.State.Api.PeekPackDetail(_packId); }
        catch (Exception ex) { AppLog.LogError("instance.remembered", ex); }
        if (remembered is null)
        {
            MetaText.Text = "Loading...";
            return;
        }
        try
        {
            _pack = remembered;
            _isOwner = App.State.OwnsPack(_packId, remembered.OwnerId);
            // Opening the instance makes its folder, as the server path does.
            App.State.Packs.EnsurePackFolder(_packId, remembered.Name, remembered.IsShared);
            ApplyPack(remembered);
            _appliedSnapshot = Snapshot(remembered);
        }
        catch (Exception ex)
        {
            // Only the head start is lost; the server's answer still follows.
            AppLog.LogError("instance.remembered", ex);
            _pack = null;
            _appliedSnapshot = null;
        }
    }

    private static string Snapshot(PackDetail pack) => JsonSerializer.Serialize(pack);

    private async Task ReloadAsync()
    {
        if (_reloading) return;
        _reloading = true;
        try
        {
            var pack = await App.State.Api.GetPackAsync(_packId);
            _pack = pack;
            _isOwner = App.State.OwnsPack(_packId, pack.OwnerId);
            App.State.Packs.EnsurePackFolder(_packId, pack.Name, pack.IsShared);
            if (pack.IsShared && pack.Rules.Count > 0)
                App.State.Rules.SyncFromServer(App.State.Packs.PackRoot(_packId), pack.Rules);

            // Redrawn only when the answer differs from what is on screen.
            var snapshot = Snapshot(pack);
            if (snapshot != _appliedSnapshot)
            {
                ApplyPack(pack);
                _appliedSnapshot = snapshot;
            }
            await TrySyncSharedContentAsync();
            _ = CheckForUpdateAsync();
        }
        catch (SessionExpiredException)
        {
            // The shell answers this itself (back to Sign In).
        }
        catch (Exception ex)
        {
            AppLog.LogError("instance", ex);
            Say(ex is ApiException { Status: HttpStatusCode.NotFound }
                ? "This instance was removed or is no longer available."
                : "This instance's details could not be loaded: " + Plain(ex), error: true);
            if (_pack is null) ApplyRecordless();
        }
        finally { _reloading = false; }
    }

    /// <summary>No record at all (the server can't be reached and nothing is remembered): the folder and
    /// the logs still work; Play needs the record.</summary>
    private void ApplyRecordless()
    {
        VersionText.Text = "";
        MetaText.Text = "Details not available";
        ModsHost.Content = Placeholder("The mods show here once this instance's details have loaded.");
        UpdateLaunchState();
    }

    private void ApplyPack(PackDetail pack)
    {
        _suppress = true;
        try
        {
            NameText.Text = pack.Name;
            ToolTip.SetTip(NameText, pack.Name);
            VersionText.Text = InstanceArt.VersionLine(pack, full: true);
            var isLocal = App.State.Api.IsLocalPack(pack.Id);
            MetaText.Text = $"{(isLocal ? "On this computer" : "by " + pack.OwnerUsername)} · updated {TimeFormat.DateTime(pack.UpdatedAt)}";
            SummaryText.Text = pack.Summary ?? "";
            SummaryText.IsVisible = !string.IsNullOrWhiteSpace(pack.Summary);
            UpdateStats();
            RefreshHeroIcon(pack.Name);
            BuildTags(pack);

            // Options: memory, JVM arguments, Java, update before play.
            var ram = App.State.Settings.GetMaxRamFor(pack.Id);
            RamSlider.Maximum = AppSettings.RamSliderMaximum(ram);
            RamSlider.Value = ram;
            UpdateRamText();
            // Not under the cursor of someone typing in it: a newer record mustn't eat their edit.
            if (!JvmArgsBox.IsFocused) JvmArgsBox.Text = App.State.Settings.GetJvmArgsFor(pack.Id);
            _ = LoadJavaChoicesAsync(pack);
            AutoUpdateBox.IsChecked = App.State.Settings.GetAutoUpdateFor(pack.Id, pack.IsShared, pack.OwnerId);
            // Only for copies you consume: for the owner or an uploader, pulling the server's copy
            // before each launch would overwrite their own work.
            var canEditPack = App.State.OwnsPack(pack.Id, pack.OwnerId)
                              || pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared);
            AutoUpdateCard.IsVisible = !canEditPack;
            ApplyLocalCard(isLocal);

            UpdateLaunchState();
        }
        finally { _suppress = false; }

        // The Mods tab follows the record: now if it is on show, else when it is next opened.
        if (SelectedTab == TabMods) LoadModsTab(force: true);
    }

    private void RefreshHeroIcon(string name)
    {
        _heroCover.Background = InstanceArt.CoverBrush(name);
        _heroLetter.Text = InstanceArt.Initial(name);
        InstanceArt.SetIcon(_heroImage, _heroLetter, InstanceArt.Icon(_packId));
    }

    private void BuildTags(PackDetail pack)
    {
        TagRow.Children.Clear();
        var background = this.TryFindResource("Surface4Brush", out var bg) && bg is IBrush b ? b : null;
        var tag = InstanceArt.Tag(pack.IsEmpty, pack.IsShared, pack.Visibility);
        TagRow.Children.Add(InstanceArt.Chip(InstanceArt.TagText(tag), InstanceArt.TagForeground(tag), background));
        if (!pack.IsEmpty && pack.Loader != LoaderKind.None)
            TagRow.Children.Add(InstanceArt.Chip(pack.Loader.ToString().ToUpperInvariant(),
                InstanceArt.TagForeground(InstanceArt.TagKind.Local), background));
    }

    private void UpdateStats()
    {
        var stats = App.State.Settings.GetPackUsage(_packId);
        if (stats.PlayCount == 0 && stats.LastPlayedAt is null)
        {
            StatsText.Text = "Not played yet";
            return;
        }
        var played = stats.PlayCount == 1 ? "Played once" : $"Played {stats.PlayCount} times";
        var total = FormatPlayTime(TimeSpan.FromSeconds(stats.TotalPlayTimeSeconds));
        var last = stats.LastPlayedAt is { } at ? " · last played " + TimeFormat.DateTime(at) : "";
        StatsText.Text = $"{played} · {total} in total{last}";
    }

    private static string FormatPlayTime(TimeSpan value)
    {
        if (value.TotalMinutes < 1) return "0 min";
        if (value.TotalHours < 1) return $"{(int)value.TotalMinutes} min";
        if (value.TotalDays < 1) return $"{(int)value.TotalHours}h {value.Minutes}m";
        return $"{(int)value.TotalDays}d {value.Hours}h";
    }

    /// <summary>The page's one message line: a plain sentence, red for an error.</summary>
    private void Say(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.IsVisible = text.Length > 0;
        StatusText.Classes.Set("danger", error);
        StatusText.Classes.Set("caption", !error);
    }

    private static string Plain(Exception ex) => InstancePlay.Sentence(
        ex is ApiException api ? ApiClient.ServerSentence(api) ?? api.Message : ex.Message);

    // ── Play, Stop and transfers ─────────────────────────────────────────────

    /// <summary>
    /// What the header offers now: Play, or Stop while the game runs; Pause and Stop in Play's place
    /// while a transfer runs (an export only reads, so it leaves Play alone); Update when the server has
    /// newer files; Upload for a shared instance this person may upload to.
    /// </summary>
    private void UpdateLaunchState()
    {
        var job = PackJobs.For(_packId);
        var transfer = job is { Kind: not PackJobKind.Export } ? job : null;
        var status = App.State.Instances.GetStatus(_packId);
        var busy = status != MinecraftInstanceStatus.Idle;
        var playable = _pack is { IsEmpty: false } && !string.IsNullOrEmpty(_pack.MinecraftVersion);

        PlayButton.IsVisible = !busy && transfer is null;
        PlayButton.IsEnabled = playable && !_launching;
        ToolTip.SetTip(PlayButton, _pack is null ? "This instance's details have not loaded yet"
            : !playable ? "This instance is empty, so there is no Minecraft to start" : null);
        StopButton.IsVisible = busy;
        StopText.Text = status == MinecraftInstanceStatus.Launching ? "Cancel" : "Stop";
        ToolTip.SetTip(StopButton, status == MinecraftInstanceStatus.Launching
            ? "Cancel the launch and stop any Minecraft it started"
            : "Stop the running Minecraft");

        PauseJobButton.IsVisible = !busy && transfer is { IsStopping: false };
        StopJobButton.IsVisible = !busy && transfer is not null;
        if (transfer is not null)
        {
            var paused = transfer.IsPaused;
            PauseJobIcon.Text = paused ? "\uE768" : "\uE769";
            PauseJobText.Text = paused ? "Resume" : "Pause";
            ToolTip.SetTip(PauseJobButton, paused
                ? $"Carry on with the {transfer.KindLabel}"
                : $"Hold the {transfer.KindLabel} where it is. Nothing already downloaded is lost.");
            StopJobText.Text = transfer.IsStopping ? "Stopping..." : "Stop";
            StopJobButton.IsEnabled = !transfer.IsStopping;
            ToolTip.SetTip(StopJobButton, $"Stop the {transfer.KindLabel} and delete the files it downloaded. Files that were already here stay.");
        }

        // While anything moves files (a transfer's Pause and Stop are up, or an upload runs) the other
        // sync action steps aside rather than sitting there disabled.
        UpdateButton.IsVisible = _pack is { IsShared: true } && _updateAvailable && job is null && _uploadCts is null;
        UpdateText.Text = _neverDownloaded ? "Download" : "Update";

        UploadButton.IsVisible = _pack is { IsShared: true } && _pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared)
                                 && (job is null || _uploadCts is not null);
        UploadText.Text = _uploadCts is not null ? "Cancel Upload" : "Upload Changes";
        UploadIcon.Text = _uploadCts is not null ? "\uE711" : "\uE898";
        ToolTip.SetTip(UploadButton, _uploadCts is not null
            ? "Stop the upload. Nothing on the server changes until it finishes."
            : "Upload the files this instance shares to CloudLauncher");
    }

    private async void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (_pack is not { } pack || _launching) return;
        if (App.State.Instances.IsBusy(_packId))
        {
            UpdateLaunchState();
            return;
        }
        _launching = true;
        UpdateLaunchState();
        try { await InstancePlay.PlayAsync(pack, Say); }
        catch (Exception ex)
        {
            AppLog.LogError("launch", ex);
            Say("Minecraft could not be started: " + Plain(ex), error: true);
        }
        finally
        {
            _launching = false;
            UpdateLaunchState();
            UpdateStats();
        }
    }

    private void OnStop(object? sender, RoutedEventArgs e)
    {
        InstancePlay.Stop(_packId, Say);
        UpdateLaunchState();
    }

    private void OnPauseJob(object? sender, RoutedEventArgs e)
    {
        if (PackJobs.For(_packId) is not { } job) return;
        if (job.IsPaused)
        {
            job.Resume();
            Say($"Resumed the {job.KindLabel}.", error: false);
        }
        else
        {
            job.Pause();
            // Said, or a bar that stands still looks like a stuck transfer.
            Say($"Paused: the {job.KindLabel} is holding where it is. Press Resume to carry on.", error: false);
        }
    }

    private void OnStopJob(object? sender, RoutedEventArgs e)
    {
        if (PackJobs.For(_packId) is not { } job) return;
        Say($"Stopping the {job.KindLabel}...", error: false);
        job.Stop();
    }

    /// <summary>Downloads the shared instance's latest files, as a job Pause and Stop drive (Windows
    /// OnDownload).</summary>
    private async void OnDownload(object? sender, RoutedEventArgs e)
    {
        if (_pack is not { } pack) return;
        // A running transfer is driven by Pause and Stop; another press would start a second one.
        if (PackJobs.IsRunning(_packId) || _uploadCts is not null)
        {
            Say(PackJobs.For(_packId) is { } running
                ? running.Kind == PackJobKind.Export
                    ? "An export is reading this instance. Update it once that has finished."
                    : $"Already busy with a {running.KindLabel}. Use Pause or Stop to interrupt it."
                : "A transfer is already running for this instance.", error: false);
            return;
        }

        var log = InstanceLog.ProgressFor(_packId);
        log.Report(_neverDownloaded ? "Downloading the shared files..." : "Updating from the server...");
        var job = PackJobs.Start(_packId, PackJobKind.Sync, pack.Name);
        try
        {
            await App.State.Packs.DownloadSharedAsync(pack.Id, log, job.Token, job);
            // PackSyncedVersion is brought up to date inside DownloadSharedAsync.
            _updateAvailable = false;
            _neverDownloaded = false;
            RefreshHeroIcon(pack.Name);
            UpdateStats();
            Say("Up to date.", error: false);
            if (SelectedTab == TabMods) LoadModsTab(force: true);
        }
        catch (OperationCanceledException)
        {
            // Stopping removes the files this run created; replaced files keep their new, verified copy.
            var removed = job.RollbackCreatedFiles();
            Say(removed > 0 ? $"Update stopped. The {removed} file(s) it had downloaded were removed." : "Update stopped.", error: false);
            ProgressHub.Clear(_packId);
        }
        catch (Exception ex)
        {
            AppLog.LogError("instance.download", ex);
            Say("The update did not finish: " + Plain(ex), error: true);
        }
        finally
        {
            PackJobs.Finish(job);
            UpdateLaunchState();
        }
    }

    /// <summary>
    /// Uploads the files the rules share. The upload is based on the version this computer last
    /// synced, not the server's current one, so the server answers 409 when this copy is behind and a
    /// stale file set never replaces collaborators' files (Windows OnUpload).
    /// </summary>
    private async void OnUpload(object? sender, RoutedEventArgs e)
    {
        if (_pack is not { } pack) return;
        // A second press on a running upload stops it.
        if (_uploadCts is not null)
        {
            Say("Cancelling the upload...", error: false);
            try { _uploadCts.Cancel(); } catch (ObjectDisposedException) { }
            return;
        }
        if (!pack.IsShared || !pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared))
        {
            Say("You can't upload to this instance: uploading needs upload rights from its owner.", error: true);
            return;
        }

        var log = InstanceLog.ProgressFor(_packId);
        var cts = new CancellationTokenSource();
        _uploadCts = cts;
        UpdateLaunchState();
        Say("Uploading your changes...", error: false);
        try
        {
            // The icon and description go up from game/.cloudlauncher, with the shared files.
            App.State.PackAssets.MirrorToSharedFolder(pack.Id);
            var packRoot = App.State.Packs.PackRoot(pack.Id);
            var rules = App.State.Rules.Load(packRoot);
            var gameDir = App.State.Packs.GameDir(pack.Id);
            var sharedPaths = await Task.Run(() => CollectSharedPaths(gameDir, rules), cts.Token);

            var baseVersion = App.State.Settings.PackSyncedVersion.TryGetValue(pack.Id, out var v) ? v : 0;
            var newVersion = await App.State.Packs.UploadSharedAsync(pack.Id, baseVersion, sharedPaths, log, cts.Token);

            // The version the server committed, so the Update button stays away for this copy.
            App.State.Settings.PackSyncedVersion[pack.Id] = newVersion;
            App.State.Settings.Save();
            Say($"Uploaded {sharedPaths.Count:N0} shared file(s). Everyone with this instance gets them with their next update.", error: false);
            await ReloadAsync();
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            Say("This instance changed on the server since you last synced. Download the latest changes, then upload again.", error: true);
            _ = CheckForUpdateAsync();
        }
        catch (OperationCanceledException)
        {
            // Nothing is committed until the server has the whole file set.
            Say("Upload cancelled. Nothing on the server changed.", error: false);
            ProgressHub.Clear(_packId);
        }
        catch (Exception ex)
        {
            AppLog.LogError("instance.upload", ex);
            Say("The upload did not finish: " + Plain(ex), error: true);
        }
        finally
        {
            _uploadCts = null;
            cts.Dispose();
            UpdateLaunchState();
        }
    }

    /// <summary>The files in game/ to upload: those a "shared" rule matches, except private ones, plus
    /// the instance's own .cloudlauncher assets.</summary>
    private static List<string> CollectSharedPaths(string gameDir, List<PackRule> rules)
    {
        var all = Directory.Exists(gameDir)
            ? Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(gameDir, f).Replace('\\', '/'))
                .OrderBy(s => s, StringComparer.Ordinal)
            : Enumerable.Empty<string>();

        var result = new List<string>();
        foreach (var rel in all)
        {
            if (rel.StartsWith(".cloudlauncher/", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(rel);
                continue;
            }
            var match = App.State.Rules.Match(rel, rules);
            if (match.IsAutoShared && !PrivateAssetPolicy.IsPrivate(rel, App.State.Settings))
                result.Add(rel);
        }
        return result;
    }

    /// <summary>
    /// Shows Update when the server has newer shared files than this computer, and Download (never
    /// started by itself) when they were never downloaded here: opening an instance to look at it is
    /// not asking for gigabytes.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        if (_pack is not { IsShared: true } pack || !pack.EffectivePermissions.HasFlag(PackPermissions.Download))
        {
            _updateAvailable = false;
            UpdateLaunchState();
            return;
        }
        try
        {
            var manifest = await App.State.Api.GetManifestAsync(pack.Id);
            var synced = App.State.Settings.PackSyncedVersion.TryGetValue(pack.Id, out var v) ? v : 0;
            _neverDownloaded = synced == 0 && manifest.Entries.Count > 0;
            _updateAvailable = manifest.Version > synced;
            if (_updateAvailable && _neverDownloaded)
            {
                var bytes = manifest.Entries.Sum(en => en.Size);
                ToolTip.SetTip(UpdateButton, $"Download this instance's files from the server ({manifest.Entries.Count:N0} files, {FormatSize(bytes)})");
                Say("Not downloaded to this computer yet. Press Download to get its files.", error: false);
            }
            else ToolTip.SetTip(UpdateButton, "Download the latest version from the server");
        }
        catch
        {
            // Offline or no access: nothing to offer.
        }
        UpdateLaunchState();
    }

    /// <summary>A collaborator's copy that was downloaded before follows the server when opened, as on
    /// Windows; the owner's never does, and a copy never downloaded waits for its Download press.</summary>
    private async Task TrySyncSharedContentAsync()
    {
        if (_pack is not { IsShared: true } pack || _isOwner) return;
        if (!pack.EffectivePermissions.HasFlag(PackPermissions.Download)) return;
        try
        {
            var manifest = await App.State.Api.GetManifestAsync(pack.Id);
            if (manifest.Entries.Count == 0) return;
            var synced = App.State.Settings.PackSyncedVersion.TryGetValue(pack.Id, out var v) ? v : 0;
            if (manifest.Version <= synced || synced == 0) return;
            if (PackJobs.IsRunning(pack.Id)) return;

            Say("Syncing the shared files...", error: false);
            var job = PackJobs.Start(_packId, PackJobKind.Sync, pack.Name);
            try
            {
                await App.State.Packs.DownloadSharedAsync(pack.Id, InstanceLog.ProgressFor(_packId), job.Token, job);
            }
            catch (OperationCanceledException)
            {
                var removed = job.RollbackCreatedFiles();
                Say(removed > 0 ? $"Update stopped. The {removed} file(s) it had downloaded were removed." : "Update stopped.", error: false);
                ProgressHub.Clear(_packId);
                return;
            }
            finally { PackJobs.Finish(job); }
            RefreshHeroIcon(pack.Name);
            UpdateStats();
            if (SelectedTab == TabMods) LoadModsTab(force: true);
            Say("Synced from the server.", error: false);
        }
        catch (Exception ex)
        {
            AppLog.LogError("instance.sync", ex);
            Say("The sync did not finish: " + Plain(ex), error: true);
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B"
    };

    // ── files to download by hand ────────────────────────────────────────────

    private async Task UpdateManualDownloadsAsync()
    {
        int waiting;
        try { waiting = await Task.Run(() => App.State.ManualDownloads.Waiting(_packId).Count); }
        catch (Exception ex)
        {
            AppLog.LogError("manual-downloads", ex);
            waiting = 0;
        }
        ManualButton.IsVisible = waiting > 0;
        ManualText.Text = $"Manual Downloads ({waiting})";
    }

    private async void OnManualDownloads(object? sender, RoutedEventArgs e)
    {
        try { await ManualDownloadsCard.ShowAsync(MainWindow.Current, _packId, _pack?.Name ?? _name); }
        catch (Exception ex) { AppLog.LogError("manual-downloads", ex); }
        _ = UpdateManualDownloadsAsync();
    }

    // ── folder ───────────────────────────────────────────────────────────────

    private string GameDir() => App.State.Packs.GameDir(_packId, _pack?.Name ?? _name);

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = GameDir();
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) Say("The folder could not be opened.", error: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("instance", ex);
            Say("The folder could not be opened: " + Plain(ex), error: true);
        }
    }

    // ── tabs ─────────────────────────────────────────────────────────────────

    private int SelectedTab => Tabs.SelectedIndex < 0 ? TabMods : Tabs.SelectedIndex;

    private void OnTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Selection changes inside the tabs (the log list, the Java box) bubble up to here.
        if (!ReferenceEquals(e.Source, Tabs)) return;
        switch (SelectedTab)
        {
            case TabMods: LoadModsTab(); break;
            case TabLog:
                if (!_logsListed) RefreshLogList();
                break;
        }
    }

    /// <summary>The installed mods (InstanceModsView), made for the record on screen.</summary>
    private void LoadModsTab(bool force = false)
    {
        if (_pack is not { } pack) return;
        if (!force && ReferenceEquals(_modsFor, pack)) return;
        try
        {
            ModsHost.Content = new InstanceModsView(pack);
            _modsFor = pack;
        }
        catch (Exception ex)
        {
            AppLog.LogError("instance.mods", ex);
            ModsHost.Content = Placeholder("The mods could not be shown: " + Plain(ex));
        }
    }

    private static Control Placeholder(string text)
    {
        var caption = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 380,
            Margin = new Thickness(0, 40)
        };
        caption.Classes.Add("caption");
        return caption;
    }

    // ── Options ──────────────────────────────────────────────────────────────

    private void OnRamSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != RangeBase.ValueProperty) return;
        UpdateRamText();
        if (_suppress || _pack is null) return;
        // Saved a moment after the slider stops moving, however it was moved.
        if (_ramSave is null)
        {
            _ramSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _ramSave.Tick += (_, _) => SaveRam();
        }
        _ramSave.Stop();
        _ramSave.Start();
    }

    /// <summary>The same value as the default is stored as "none of its own", so the instance follows
    /// the default if that changes later.</summary>
    private void SaveRam()
    {
        _ramSave?.Stop();
        if (_suppress || _pack is null) return;
        var mb = (int)RamSlider.Value;
        App.State.Settings.SetMaxRamFor(_pack.Id, mb == App.State.Settings.EffectiveDefaultMaxRamMb ? null : mb);
        App.State.Settings.Save();
    }

    private void UpdateRamText()
    {
        var mb = (int)RamSlider.Value;
        var fallback = App.State.Settings.EffectiveDefaultMaxRamMb;
        RamText.Text = $"{mb} MB";
        RamHint.Text = mb == fallback ? "The launcher's default." : $"The launcher's default is {fallback} MB.";
        RamDefaultButton.IsVisible = mb != fallback;
    }

    private void OnRamDefault(object? sender, RoutedEventArgs e)
    {
        RamSlider.Value = App.State.Settings.EffectiveDefaultMaxRamMb;
        SaveRam();
    }

    private void SaveJvmArgs()
    {
        if (_suppress || _pack is null) return;
        var text = (JvmArgsBox.Text ?? "").Trim();
        if (text == App.State.Settings.GetJvmArgsFor(_pack.Id)) return;
        App.State.Settings.SetJvmArgsFor(_pack.Id, text);
        App.State.Settings.Save();
    }

    private void OnAutoUpdateToggled(object? sender, RoutedEventArgs e)
    {
        if (_suppress || _pack is null) return;
        App.State.Settings.PackAutoUpdate[_pack.Id] = AutoUpdateBox.IsChecked == true;
        App.State.Settings.Save();
    }

    /// <summary>For an instance on this computer only: sign in, or (signed in) add it to the account.</summary>
    private void ApplyLocalCard(bool isLocal)
    {
        LocalCard.IsVisible = isLocal;
        if (!isLocal) return;
        var signedIn = App.State.Api.IsSignedIn;
        LocalText.Text = signedIn
            ? "This instance was made without a CloudLauncher account, so it lives on this computer only. Add it "
              + "to your account to share it or sync it to your other computers. Its files stay here."
            : "This instance is not on a CloudLauncher account, so it can't be shared or synced. Playing, mods "
              + "and everything else work the same. Sign in to add it to an account.";
        LocalButton.Content = signedIn ? "Add to Account" : "Sign In";
        LocalButton.IsEnabled = true;
        LocalStatus.IsVisible = false;
    }

    private async void OnLocalAction(object? sender, RoutedEventArgs e)
    {
        if (!App.State.Api.IsSignedIn)
        {
            MainWindow.Current.Navigate(new SignInPage());
            return;
        }

        LocalButton.IsEnabled = false;
        LocalStatus.Classes.Set("danger", false);
        LocalStatus.Text = "Adding it to your account...";
        LocalStatus.IsVisible = true;
        try
        {
            var added = await App.State.Api.AddLocalPackToAccountAsync(_packId);
            MainWindow.Current.Toast($"“{added.Name}” is on your account now.");
            _appliedSnapshot = null;   // drawn again from the account's record
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            AppLog.LogError("instance.local", ex);
            LocalStatus.Classes.Set("danger", true);
            LocalStatus.Text = "It could not be added: " + Plain(ex);
            LocalButton.IsEnabled = true;
        }
    }

    private enum JavaKind { Auto, Install, Browse }

    private sealed record JavaChoice(string Label, string? Path, JavaKind Kind);

    /// <summary>Automatic (or the launcher's default Java), each Java found on this computer, and
    /// Browse (the Windows JavaPicker).</summary>
    private async Task LoadJavaChoicesAsync(PackDetail pack)
    {
        _javaLoading = true;
        try
        {
            var required = LaunchService.RequiredJavaMajorFor(pack.MinecraftVersion);
            var launcherDefault = App.State.Settings.DefaultJavaPath;
            var autoLabel = string.IsNullOrWhiteSpace(launcherDefault) || !File.Exists(launcherDefault)
                ? $"Automatic  ·  Java {required} for Minecraft {pack.MinecraftVersion}"
                : $"Launcher Default  ·  {launcherDefault}";
            var choices = new List<JavaChoice> { new(autoLabel, null, JavaKind.Auto) };

            IReadOnlyList<LaunchService.JavaInstall> installs;
            try { installs = await JavaInstallsSource(); }
            catch (Exception ex)
            {
                AppLog.Log("instance", "Java installations could not be listed: " + ex.Message);
                installs = [];
            }
            if (_pack?.Id != pack.Id) return;

            foreach (var j in installs)
                choices.Add(new($"Java {j.Major}  ·  {j.Path}{(j.Managed ? "  (downloaded by the launcher)" : "")}", j.Path, JavaKind.Install));

            // A stored path that isn't among the ones found is still shown, so it can be kept.
            App.State.Settings.PackJavaPath.TryGetValue(pack.Id, out var current);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            JavaChoice? match = null;
            if (!string.IsNullOrWhiteSpace(current))
            {
                match = choices.FirstOrDefault(c => c.Kind == JavaKind.Install && string.Equals(c.Path, current, comparison));
                if (match is null)
                {
                    match = new JavaChoice($"Java  ·  {current}", current, JavaKind.Install);
                    choices.Add(match);
                }
            }
            choices.Add(new JavaChoice("Browse...", null, JavaKind.Browse));

            JavaBox.ItemsSource = choices.Select(c => new ComboBoxItem { Content = c.Label, Tag = c }).ToList();
            JavaBox.SelectedIndex = choices.IndexOf(match ?? choices[0]);
            UpdateJavaHint();
        }
        catch (Exception ex)
        {
            AppLog.LogError("instance.java", ex);
            JavaHint.Text = "The Java installations could not be listed: " + Plain(ex);
        }
        finally { _javaLoading = false; }
    }

    private async void OnJavaChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_javaLoading || _suppress || _pack is not { } pack) return;
        if ((JavaBox.SelectedItem as ComboBoxItem)?.Tag is not JavaChoice choice) return;

        if (choice.Kind == JavaKind.Browse)
        {
            var picked = await BrowseForJavaAsync();
            if (picked is not null)
            {
                App.State.Settings.SetJavaPathFor(pack.Id, picked);
                App.State.Settings.Save();
            }
            _ = LoadJavaChoicesAsync(pack);   // re-listed (and re-selected) either way
            return;
        }

        App.State.Settings.SetJavaPathFor(pack.Id, choice.Path);
        App.State.Settings.Save();
        UpdateJavaHint();
    }

    private async Task<string?> BrowseForJavaAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return null;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a Java Executable",
                AllowMultiple = false
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        }
        catch (Exception ex)
        {
            AppLog.LogError("instance.java", ex);
            Say("The file picker could not be opened: " + Plain(ex), error: true);
            return null;
        }
    }

    private void UpdateJavaHint()
    {
        if (_pack is null) return;
        var path = App.State.Settings.GetJavaPathFor(_pack.Id);
        JavaHint.Text = path is null
            ? "Launches with the Java this Minecraft release needs, downloaded when it is missing. From the next launch."
            : $"Launches with {path}. From the next launch.";
    }

    // ── Log ──────────────────────────────────────────────────────────────────

    /// <summary>This session's output and the launcher's log first, then the game's crash reports and
    /// logs, newest first (the Windows Logs tab's list).</summary>
    private void RefreshLogList()
    {
        _logsListed = true;
        var previous = (LogList.SelectedItem as InstanceLogSource)?.Key;
        var rows = new List<InstanceLogSource>
        {
            new("game", "Game Output", "This session: launch, game, transfers", "\uE756", null, false),
            new("launcher", "Launcher Log", "What the launcher is doing", "\uE8A5", null, false)
        };
        var files = new List<(InstanceLogSource Row, DateTime At)>();
        try
        {
            var gameDir = GameDir();
            Collect(Path.Combine(gameDir, "logs"), "*.log*", crash: false);
            Collect(Path.Combine(gameDir, "crash-reports"), "crash-*.txt", crash: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("instance.logs", ex);
            Say("The game's logs could not be listed: " + Plain(ex), error: true);
        }
        rows.AddRange(files.OrderByDescending(f => f.Row.IsCrash).ThenByDescending(f => f.At).Select(f => f.Row));

        LogList.ItemsSource = rows;
        LogList.SelectedItem = rows.FirstOrDefault(r => r.Key == previous) ?? rows[0];

        void Collect(string dir, string pattern, bool crash)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in new DirectoryInfo(dir).GetFiles(pattern))
            {
                var meta = (crash ? "Crash report · " : "") + $"{TimeFormat.DateTime(f.LastWriteTime)} · {FormatSize(f.Length)}";
                files.Add((new InstanceLogSource("file:" + f.FullName, f.Name, meta, crash ? "\uE7BA" : "\uE8A5", f.FullName, crash),
                    f.LastWriteTime));
            }
        }
    }

    private void OnRefreshLogs(object? sender, RoutedEventArgs e) => RefreshLogList();

    private async void OnLogSelected(object? sender, SelectionChangedEventArgs e)
    {
        AppLog.MessageAppended -= OnLauncherLogLine;
        var row = LogList.SelectedItem as InstanceLogSource;
        _logShown = row;
        _logDirty = false;
        var read = ++_logRead;
        if (row is null)
        {
            ShowLog("", "", "", null);
            return;
        }

        switch (row.Key)
        {
            case "game":
                ShowLog(row.Name, row.Meta, InstanceLog.Text(_packId),
                    "Nothing yet. The launch, the game's own output and file transfers show here while they run.");
                break;
            case "launcher":
                ShowLog(row.Name, row.Meta, AppLog.Buffer, "The launcher has logged nothing yet.");
                if (_attached) AppLog.MessageAppended += OnLauncherLogLine;
                break;
            default:
                ShowLog(row.Name, row.Meta, "", "Reading...");
                string text;
                try
                {
                    // A modded latest.log can be tens of megabytes: read off the UI thread.
                    var path = row.Path!;
                    text = await Task.Run(() => ReadLogText(path));
                }
                catch (Exception ex)
                {
                    text = "";
                    if (read == _logRead) ShowLog(row.Name, row.Meta, "", "This log could not be read: " + Plain(ex));
                    return;
                }
                if (read != _logRead) return;   // another row was picked meanwhile
                ShowLog(row.Name, row.Meta, text, "This log is empty.");
                break;
        }
    }

    private void ShowLog(string title, string meta, string text, string? whenEmpty)
    {
        LogTitle.Text = title;
        LogMeta.Text = meta;
        LogText.Text = text;
        LogEmpty.Text = whenEmpty ?? "";
        LogEmpty.IsVisible = text.Length == 0 && !string.IsNullOrEmpty(whenEmpty);
        // The end is where the failure is.
        Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OnInstanceLogAppended(Guid packId, string text)
    {
        if (packId == _packId && _logShown?.Key == "game") _logDirty = true;
    }

    private void OnInstanceLogCleared(Guid packId)
    {
        if (packId == _packId && _logShown?.Key == "game") _logDirty = true;
    }

    /// <summary>Launcher log lines arrive from any thread.</summary>
    private void OnLauncherLogLine(string line) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_logShown?.Key == "launcher") _logDirty = true;
        });

    /// <summary>Redraws a live log at most four times a second; a modded game prints hundreds of lines
    /// a second while it starts.</summary>
    private void FlushLog()
    {
        if (!_logDirty || _logShown is null) return;
        _logDirty = false;
        var atEnd = LogScroll.Offset.Y >= LogScroll.Extent.Height - LogScroll.Viewport.Height - 24;
        var text = _logShown.Key == "game" ? InstanceLog.Text(_packId) : AppLog.Buffer;
        LogText.Text = text;
        LogEmpty.IsVisible = text.Length == 0;
        if (atEnd) Dispatcher.UIThread.Post(() => LogScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>A log file as text: un-gzipped when rolled, and only its last 200,000 characters when
    /// long, since the end is where the failure is.</summary>
    private static string ReadLogText(string path)
    {
        string text;
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            text = reader.ReadToEnd();
        }
        else
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            text = reader.ReadToEnd();
        }
        const int maxChars = 200_000;
        if (text.Length > maxChars) text = "[...older lines trimmed...]\n" + text[^maxChars..];
        return text.Replace("\r\n", "\n");
    }

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        var text = LogText.Text ?? "";
        if (text.Length == 0)
        {
            Say("There is nothing to copy.", error: false);
            return;
        }
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
                MainWindow.Current.Toast("Copied the log.");
            }
        }
        catch (Exception ex)
        {
            AppLog.LogError("instance.logs", ex);
            Say("The log could not be copied: " + Plain(ex), error: true);
        }
    }

    private void OnOpenLogsFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(GameDir(), "logs");
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir)) Say("The folder could not be opened.", error: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("instance.logs", ex);
            Say("The folder could not be opened: " + Plain(ex), error: true);
        }
    }
}
