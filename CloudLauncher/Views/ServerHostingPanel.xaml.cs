using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;

namespace CloudLauncher.Views;

/// <summary>Which access list the Players tab is showing.</summary>
public enum ServerAccessList { Ops = 0, Whitelist = 1, BannedPlayers = 2, BannedIps = 3 }

/// <summary>One row of an access list, flattened for the template.</summary>
public sealed class ServerAccessRow
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string RemoveHint { get; init; } = "";
}

/// <summary>
/// Hosting one instance as a dedicated server on this PC: install it, start and stop it, watch its
/// console, edit its settings and access lists, and see which parts of the pack reach it.
/// </summary>
/// <remarks>
/// The Hosting tab of <see cref="FileManagementView"/>. Everything here is local and works offline;
/// the only network call is a best-effort Mojang UUID lookup when a name is added to a list. The
/// process side lives in <see cref="ServerHostService"/>.
/// </remarks>
public sealed partial class ServerHostingPanel : UserControl
{
    /// <summary>
    /// The app-wide server tracker: <c>App.State.ServerHost</c> when the shell owns one, otherwise
    /// one of this page's own.
    /// </summary>
    /// <remarks>
    /// A server outlives the page that started it, so there must be one tracker per app, or the
    /// instance page's status chip would watch a different service. The property is looked up by
    /// reflection once and cached; read <c>App.State.ServerHost</c> directly once AppState always has it.
    /// </remarks>
    public static ServerHostService Host => _host ??= ResolveHost();

    private static ServerHostService? _host;

    private static ServerHostService ResolveHost()
    {
        var shared = typeof(AppState).GetProperty("ServerHost")?.GetValue(App.State) as ServerHostService;
        if (shared is not null) return shared;
        AppLog.Log("server-host", "AppState has no ServerHost yet; the hosting page is using its own.");
        return new ServerHostService(App.State.Settings, App.State.Packs, App.State.Launcher);
    }

    /// <summary>Command history per instance, newest last. Kept for the session rather than on disk:
    /// a server command is not a setting, and the RCON store in AppSettings is keyed by address.</summary>
    private static readonly Dictionary<Guid, List<string>> CommandHistories = new();

    private const int MaxCommandHistory = 60;

    private readonly MainWindow _shell;
    private readonly PackDetail _pack;
    private readonly bool _canManage;
    private readonly string _runDir;
    private readonly string _overrideDir;

    private readonly ServerPropertiesEditor _props;
    private readonly PageState _playersState;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Each control's own tooltip, kept so <see cref="Enable"/> can put it back.</summary>
    private readonly Dictionary<FrameworkElement, object?> _tips = new();

    private CancellationTokenSource? _work;
    private List<string> _history = new();
    private int _historyIndex;

    private bool _loaded;
    private bool _filling;
    private bool _follow = true;
    private bool _busy;

    private bool _installed;
    private int _port = 25565;
    private string? _lanAddress;
    private int _tickCount;
    private (int Online, int Max)? _players;
    private IReadOnlyCollection<string>? _clientOnlyJars;
    private bool _clientOnlyRead;

    /// <summary>Wording for the Players tab's four lists. Kept here since only this page uses it.</summary>
    private static readonly PageCopy AccessCopy = new()
    {
        Glyph = "",
        FilteredGlyph = "",
        Verb = "Reading",
        Noun = "entries",
        LoadingLine = "Reading the server's ops, whitelist and ban lists.",
        EmptyTitle = "Nothing on this list",
        EmptyBody = "Add a player by name above.",
        FilteredTitle = "Nothing matched",
        FilteredBody = "No entry matched that.",
        ErrorTitle = "Could not read the server's lists",
        OfflineTitle = "Showing the lists on this PC",
        OfflineBody = "The server is not answering ({0}). These files are local, so they are still "
                    + "readable and editable."
    };

    public ServerHostingPanel(MainWindow shell, PackDetail pack)
    {
        InitializeComponent();
        _shell = shell;
        _pack = pack;

        // Read-only collaborators on someone else's shared instance may not write to it. Your own
        // instances, including local never-uploaded ones, are always yours to host.
        _canManage = !pack.IsShared
                     || pack.OwnerId == App.State.Settings.UserId
                     || pack.EffectivePermissions.HasFlag(PackPermissions.UploadShared);

        _runDir = App.State.Packs.ServerRunDir(pack.Id);
        _overrideDir = App.State.Packs.ServerOverrideDir(pack.Id);

        _props = new ServerPropertiesEditor(shell, _canManage);
        _props.Status += SetStatus;
        _props.PortChanged += port => { _port = port; PaintConnectHint(); };
        PropertiesHost.Child = _props;

        _playersState = new PageState(EntryListCard, PlayersStateHost, nameof(ServerHostingPanel))
            .Copy(AccessCopy)
            .Slots(ListCountLabel);
        _playersState.RetryRequested += () => _ = LoadAccessListAsync();

        ListKindBox.SelectedIndex = 0;
        ConsoleLog.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnConsoleScrolled));
        ConsoleLog.StatsChanged += () => ConsoleEmptyHint.Visibility =
            ConsoleLog.TotalLineCount == 0 ? Visibility.Visible : Visibility.Collapsed;

        _history = CommandHistories.TryGetValue(pack.Id, out var stored) ? stored : new List<string>();
        _historyIndex = _history.Count;

        OverrideFiles.Root = _overrideDir;
        OverrideFiles.FileActivated += OnOverrideFileActivated;

        if (!_canManage) DisableForReadOnly();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Convenience for the host page, which already holds both of these.</summary>
    public ServerHostingPanel(FileManagementView page) : this(page.Shell, page.Pack) { }

    // ── lifetime ─────────────────────────────────────────────────────────────

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Host.StateChanged += OnServerStateChanged;
        Host.LinesReceived += OnServerLines;
        _tick.Tick += OnTick;
        _tick.Start();

        if (_loaded) { Paint(); return; }
        _loaded = true;
        _ = LoadAsync();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        Host.StateChanged -= OnServerStateChanged;
        Host.LinesReceived -= OnServerLines;
        _tick.Tick -= OnTick;
        _tick.Stop();
        // The server keeps running after the page closes; only this page's own reads (mirror
        // preview, list loads) are cancelled.
        _work?.Cancel();
        _work = null;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F5 && !_busy)
        {
            e.Handled = true;
            _ = LoadAsync();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // ── loading ──────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        var ct = _work.Token;

        Busy(true, "Reading the server folder...");
        try
        {
            _installed = await Task.Run(() => SafeIsInstalled(), ct);
            _lanAddress ??= await Task.Run(LocalIPv4, ct);
            if (ct.IsCancellationRequested) return;

            _props.Load(_runDir);
            _port = _props.Port;

            FillSettings();
            FillEula();
            PaintListKindChrome();
            Paint();

            BackfillConsole();
            EnsureOverrideFolder();
            OverrideFiles.Refresh();

            await LoadAccessListAsync();
            if (ct.IsCancellationRequested) return;
            await RefreshMirrorAsync(ct);

            // Replace the "Reading the server folder..." status now that the read is done.
            SetStatus(_installed
                ? ""
                : "This instance has never been set up as a server. Install writes server-run/.");
        }
        catch (OperationCanceledException) { /* the page moved on */ }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-ui", ex);
            SetStatus("The server folder could not be read. The full error is in the launcher log.");
        }
        finally
        {
            Busy(false, null);
            Paint();
        }
    }

    private bool SafeIsInstalled()
    {
        try { return App.State.Launcher.IsServerInstalled(_pack); }
        catch (Exception ex) { AppLog.LogError("server-host-ui", ex); return false; }
    }

    private void EnsureOverrideFolder()
    {
        try { Directory.CreateDirectory(_overrideDir); }
        catch (Exception ex) { AppLog.LogError("server-host-ui", ex); }
    }

    private void FillSettings()
    {
        _filling = true;
        try
        {
            var settings = App.State.Settings;
            var ram = settings.GetServerMaxRamFor(_pack.Id);
            // Keep the slider's 32 GB ceiling unless the pack already asks for more. Deriving it from the
            // current value would park the thumb at the far right.
            RamSlider.Maximum = Math.Max(RamSlider.Maximum, RoundUpToTick(ram));
            RamSlider.Value = Math.Clamp(ram, RamSlider.Minimum, RamSlider.Maximum);
            JvmArgsBox.Text = settings.GetServerJvmArgsFor(_pack.Id);
            AutoRestartCheck.IsChecked = settings.GetServerAutoRestart(_pack.Id);
            PaintRamLabel();
        }
        finally { _filling = false; }
    }

    private static double RoundUpToTick(int mb) => Math.Ceiling(mb / 512.0) * 512.0;

    private void FillEula()
    {
        _filling = true;
        try
        {
            var inSettings = App.State.Settings.GetServerEulaAccepted(_pack.Id);
            var onDisk = LaunchService.EulaAcceptedOnDisk(_runDir);
            EulaCheck.IsChecked = inSettings || onDisk;
            EulaNote.Text = (inSettings, onDisk) switch
            {
                (true, _) => "Accepted for this instance. eula.txt is written into the server folder when it is installed.",
                (false, true) => "An eula.txt in the server folder already says eula=true, so this instance can start. "
                               + "Untick this to take that back.",
                _ => "A Minecraft server refuses to start until its EULA is accepted. The launcher will not tick this for you."
            };
        }
        finally { _filling = false; }
    }

    private void BackfillConsole()
    {
        var lines = Host.GetConsole(_pack.Id);
        if (lines.Count == 0) return;
        ConsoleLog.SetText(string.Join("\n", lines.Select(l => l.Text)));
        ConsoleLog.ScrollToEnd();
    }

    // ── painting ─────────────────────────────────────────────────────────────

    private void OnServerStateChanged(Guid packId)
    {
        if (packId != _pack.Id) return;
        // An install, crash or stop changes the folder, so re-read what we show from it.
        _installed = _installed || Host.GetStatus(packId).Install is not null;
        Paint();
    }

    private void OnServerLines(Guid packId, IReadOnlyList<ServerLogLine> batch)
    {
        if (packId != _pack.Id || batch.Count == 0) return;
        ConsoleLog.Append(string.Join("\n", batch.Select(l => l.Text)));
        if (_follow) ConsoleLog.ScrollToEnd();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var state = Host.GetStatus(_pack.Id);
        if (state.Status is ServerStatus.Starting or ServerStatus.Running or ServerStatus.Stopping)
            PaintStatusDetail(state);

        _tickCount++;
        if (state.Status == ServerStatus.Running && _tickCount % 10 == 0) _ = PingAsync(state.Port);
        else if (state.Status != ServerStatus.Running) _players = null;
    }

    private void Paint()
    {
        var state = Host.GetStatus(_pack.Id);
        var playable = !_pack.IsEmpty && !string.IsNullOrEmpty(_pack.MinecraftVersion);

        var (badge, brushKey) = state.Status switch
        {
            ServerStatus.Installing => ("Installing", "WarningBrush"),
            ServerStatus.Starting => ("Starting", "WarningBrush"),
            ServerStatus.Running => ("Running", "SuccessBrush"),
            ServerStatus.Stopping => ("Stopping", "WarningBrush"),
            ServerStatus.Crashed => ("Crashed", "DangerBrush"),
            _ => (_installed ? "Installed" : "Not installed", "TextSecondaryBrush")
        };
        StatusBadgeText.Text = badge;
        StatusBadgeText.SetResourceReference(ForegroundProperty, brushKey);

        StatusTitle.Text = state.Status switch
        {
            ServerStatus.Running => $"{_pack.Name} is up",
            ServerStatus.Crashed => $"{_pack.Name} crashed",
            ServerStatus.Installing or ServerStatus.Starting => $"{_pack.Name} is coming up",
            ServerStatus.Stopping => $"{_pack.Name} is shutting down",
            _ => _pack.Name
        };

        PaintStatusDetail(state);
        PaintConnectHint();

        CrashPanel.Visibility = state.Status == ServerStatus.Crashed && state.LastLines.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (CrashPanel.Visibility == Visibility.Visible)
        {
            CrashTitle.Text = state.Message ?? "The server exited on its own.";
            CrashLines.Text = string.Join(Environment.NewLine, state.LastLines.TakeLast(6));
        }

        var live = state.IsLive;
        Enable(InstallButton, _canManage && playable && !live && !_busy, WhyDisabled(playable, live));
        Enable(StartButton, _canManage && playable && !live && !_busy, WhyDisabled(playable, live));
        Enable(StopButton, _canManage && live, live ? null : "The server is not running.");
        Enable(RestartButton, _canManage && playable && state.Status is ServerStatus.Running or ServerStatus.Crashed,
               state.Status is ServerStatus.Running or ServerStatus.Crashed ? null : "There is nothing to restart yet.");

        // While a stop is pending, the Stop button becomes Force stop.
        var forcing = state.Status == ServerStatus.Stopping;
        StopLabel.Text = forcing ? "Force stop" : "Stop";
        StopGlyph.Text = forcing ? "" : "";

        var running = state.Status == ServerStatus.Running;
        SendButton.IsEnabled = running && _canManage;
        CommandBox.IsEnabled = running && _canManage;
        CommandBox.ToolTip = running
            ? "Type a server command. Up and down walk back through what you have run here."
            : "The console takes commands while the server is running.";
        ToolTipService.SetShowOnDisabled(CommandBox, true);

        _props.SetRunning(running);
        AddEntryButton.IsEnabled = _canManage;
    }

    private string? WhyDisabled(bool playable, bool live)
    {
        if (!_canManage) return "You have read-only access to this instance, so you cannot host it.";
        if (_pack.IsEmpty) return "This instance has no files of its own, so there is nothing to serve.";
        if (!playable) return "This instance has no Minecraft version set, so no server can be installed for it.";
        if (live) return "The server is already running.";
        return null;
    }

    /// <summary>
    /// Enables or disables a control, and while it is off replaces its tooltip with the reason.
    /// </summary>
    /// <remarks>The original tooltip is remembered the first time and restored on enable.</remarks>
    private void Enable(FrameworkElement element, bool enabled, string? why)
    {
        if (!_tips.ContainsKey(element)) _tips[element] = element.ToolTip;
        element.IsEnabled = enabled;
        ToolTipService.SetShowOnDisabled(element, true);
        element.ToolTip = !enabled && why is { Length: > 0 } ? why : _tips[element];
    }

    private void PaintStatusDetail(ServerState state)
    {
        var parts = new List<string>();
        switch (state.Status)
        {
            case ServerStatus.Running:
            case ServerStatus.Stopping:
                if (state.ProcessId is { } pid) parts.Add($"PID {pid}");
                if (state.Uptime is { } up) parts.Add("up " + Describe(up));
                parts.Add($"port {state.Port}");
                if (_players is { } p) parts.Add($"{p.Online} of {p.Max} player(s)");
                if (state.RestartCount > 0) parts.Add($"restarted {state.RestartCount}×");
                break;

            case ServerStatus.Installing:
            case ServerStatus.Starting:
                if (state.Message is { Length: > 0 }) parts.Add(state.Message);
                if (state.ProcessId is { } startingPid) parts.Add($"PID {startingPid}");
                break;

            case ServerStatus.Crashed:
                parts.Add(state.Message ?? "It exited on its own.");
                parts.Add("The console has the whole run.");
                break;

            default:
                parts.Add(_installed
                    ? $"{DescribeLoader()} is installed in server-run."
                    : $"Not installed yet. Install mirrors the pack into server-run and fetches the {DescribeLoader()} server.");
                if (state.LastExitCode is { } code)
                    parts.Add(code == 0 ? "The last run stopped cleanly." : $"The last run exited with code {code}.");
                break;
        }
        StatusDetail.Text = string.Join(" · ", parts);
    }

    private string DescribeLoader()
    {
        var mc = string.IsNullOrEmpty(_pack.MinecraftVersion) ? "" : " " + _pack.MinecraftVersion;
        return _pack.Loader switch
        {
            LoaderKind.None => "A vanilla" + mc + " server",
            _ => $"{_pack.Loader} {_pack.LoaderVersion}{(mc.Length > 0 ? " for" + mc : "")}"
        };
    }

    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s"
        : $"{span.Seconds}s";

    private void PaintConnectHint()
    {
        var port = Host.GetStatus(_pack.Id).Status == ServerStatus.Stopped ? _port : Host.GetStatus(_pack.Id).Port;
        if (port <= 0) port = _port;

        var hint = $"On this PC: localhost:{port}";
        if (_lanAddress is { Length: > 0 }) hint += $"  ·  On your network: {_lanAddress}:{port}";
        hint += "  ·  Anyone outside your network needs that port forwarded.";
        ConnectLabel.Text = hint;
        ConnectRow.Visibility = Visibility.Visible;
    }

    private void PaintRamLabel()
    {
        var mb = (int)RamSlider.Value;
        var own = App.State.Settings.HasServerMaxRamOverride(_pack.Id);
        RamValueLabel.Text = own
            ? $"{mb:N0} MB for the server"
            : $"{mb:N0} MB - the same as the client";
        Enable(RamInheritButton, own && _canManage,
               own ? null : "The server already follows the instance's own memory setting.");
    }

    private void Busy(bool busy, string? line)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (line is not null) SetStatus(line);
    }

    private void SetStatus(string text) => StatusLabel.Text = text;

    private void DisableForReadOnly()
    {
        const string why = "You have read-only access to this instance, so you cannot host it or "
                         + "change its server files.";
        foreach (var element in new FrameworkElement[]
                 { EulaCheck, RamSlider, JvmArgsBox, AutoRestartCheck, RamInheritButton,
                   WhitelistOnCheck, EntryNameBox, EntryReasonBox, AddEntryButton })
        {
            element.IsEnabled = false;
            element.ToolTip = why;
            ToolTipService.SetShowOnDisabled(element, true);
        }
    }

    // ── install / start / stop ───────────────────────────────────────────────

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_canManage) return;
            _work?.Cancel();
            _work = new CancellationTokenSource();
            var ct = _work.Token;

            Busy(true, "Installing the server...");
            var clientOnly = await ClientOnlyJarsAsync(ct);
            var log = new Progress<string>(line =>
            {
                SetStatus(line.Length > 140 ? line[..140] + "..." : line);
                ConsoleLog.Append(line);
                if (_follow) ConsoleLog.ScrollToEnd();
            });

            var install = await App.State.Launcher.EnsureServerInstalledAsync(_pack, log, clientOnly, ct);
            _installed = true;
            _port = install.Port;
            _props.Load(_runDir);
            SetStatus($"The server is installed in {install.ServerRunDir}.");
            await RefreshMirrorAsync(ct);
        }
        catch (OperationCanceledException) { SetStatus("Install cancelled."); }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-ui", ex);
            SetStatus("The server could not be installed. The full error is in the launcher log, and "
                    + "the console above has the installer's own output.");
        }
        finally { Busy(false, null); Paint(); }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_canManage) return;

            if (_props.IsDirty)
            {
                var save = await AppDialog.ConfirmAsync(_shell, "Unsaved server settings",
                    "You changed server.properties on the Server settings tab and have not saved it. "
                    + "A server reads that file as it starts, so anything unsaved will not apply to "
                    + "this run.",
                    "Save and start", "Start anyway");
                if (save && !await _props.SaveAsync()) return;
            }

            if (!App.State.Launcher.IsServerEulaAccepted(_pack.Id))
            {
                await AppDialog.MessageAsync(_shell, "Accept the Minecraft EULA first",
                    "A Minecraft server will not run until its EULA has been accepted. Read it at "
                    + LaunchService.ServerEulaNotAcceptedException.EulaUrl + ", then tick "
                    + "'I accept the Minecraft EULA' at the top of this page.");
                EulaCheck.Focus();
                return;
            }

            _work?.Cancel();
            _work = new CancellationTokenSource();
            var ct = _work.Token;

            Busy(true, "Preparing the server...");
            var clientOnly = await ClientOnlyJarsAsync(ct);
            var log = new Progress<string>(line => SetStatus(line.Length > 140 ? line[..140] + "..." : line));

            // The service already copies the install log into the console; appending here too would
            // show it twice.
            var state = await Host.StartAsync(_pack, log, clientOnly, ct);
            _installed = true;
            _port = state.Port;
            _props.Load(_runDir);
            SetStatus($"Started. Players connect on port {state.Port}.");
        }
        catch (OperationCanceledException) { SetStatus("Start cancelled."); }
        catch (LaunchService.ServerEulaNotAcceptedException)
        {
            FillEula();
            await AppDialog.MessageAsync(_shell, "Accept the Minecraft EULA first",
                "A Minecraft server will not run until its EULA has been accepted. Tick "
                + "'I accept the Minecraft EULA' at the top of this page.");
        }
        catch (InvalidOperationException ex)
        {
            // These ("already running", "empty pack", "no Minecraft version") are written for people,
            // so their text is shown as is.
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-ui", ex);
            SetStatus("The server could not be started. The full error is in the launcher log, and "
                    + "the console above has its own output.");
        }
        finally { Busy(false, null); Paint(); }
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        try
        {
            var state = Host.GetStatus(_pack.Id);
            if (!state.IsLive) return;
            var force = state.Status == ServerStatus.Stopping;

            var ok = force
                ? await AppDialog.ConfirmAsync(_shell, "Kill the server",
                    "The server is already saving and shutting down. Killing it now ends the JVM "
                    + "immediately: anything it had not finished writing - the chunks around each "
                    + "player, and player inventories - is lost.",
                    "Kill it", "Wait", danger: true)
                : await AppDialog.ConfirmAsync(_shell, "Stop the server",
                    state.Status == ServerStatus.Running
                        ? "Everyone playing on it is disconnected. The world is saved first, and the "
                        + "server is given up to 90 seconds to finish before it is killed."
                        : "The server is still starting up. Stopping now cancels the run.",
                    "Stop", "Keep it running");
            if (!ok) return;

            Busy(true, force ? "Killing the server..." : "Stopping the server...");
            await Host.StopAsync(_pack.Id, force);
            SetStatus(force ? "The server was killed." : "The server stopped.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-ui", ex);
            SetStatus("The server could not be stopped. The full error is in the launcher log.");
        }
        finally { Busy(false, null); Paint(); }
    }

    private async void OnRestart(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_canManage) return;
            var state = Host.GetStatus(_pack.Id);
            if (state.Status == ServerStatus.Running)
            {
                var ok = await AppDialog.ConfirmAsync(_shell, "Restart the server",
                    "Everyone playing on it is disconnected while it comes back. The world is saved "
                    + "before it stops.",
                    "Restart", "Cancel");
                if (!ok) return;
            }

            _work?.Cancel();
            _work = new CancellationTokenSource();
            var ct = _work.Token;

            Busy(true, "Restarting the server...");
            var clientOnly = await ClientOnlyJarsAsync(ct);
            var log = new Progress<string>(line => SetStatus(line.Length > 140 ? line[..140] + "..." : line));
            var next = await Host.RestartAsync(_pack, log, clientOnly, ct);
            _port = next.Port;
            SetStatus($"Restarted. Players connect on port {next.Port}.");
        }
        catch (OperationCanceledException) { SetStatus("Restart cancelled."); }
        catch (LaunchService.ServerEulaNotAcceptedException)
        {
            FillEula();
            SetStatus("The Minecraft EULA has not been accepted for this instance, so it cannot start.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-ui", ex);
            SetStatus("The server could not be restarted. The full error is in the launcher log.");
        }
        finally { Busy(false, null); Paint(); }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void OnOpenServerFolder(object sender, RoutedEventArgs e) => OpenInExplorer(_runDir);

    private void OnOpenOverrideFolder(object sender, RoutedEventArgs e) => OpenInExplorer(_overrideDir);

    private void OpenInExplorer(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            if (!SafeLaunch.OpenFolder(dir))
                SetStatus("That folder could not be opened. The full error is in the launcher log.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-ui", ex);
            SetStatus("That folder could not be opened. The full error is in the launcher log.");
        }
    }

    /// <summary>A text or image file opens in its default app; anything else, a server jar included,
    /// is shown in Explorer rather than run.</summary>
    private void OnOverrideFileActivated(string relativePath)
    {
        try
        {
            var full = Path.Combine(_overrideDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return;
            if (!SafeLaunch.OpenFile(full))
                SetStatus("That file could not be opened. The full error is in the launcher log.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-ui", ex);
            SetStatus("That file could not be opened. The full error is in the launcher log.");
        }
    }

    // ── EULA ─────────────────────────────────────────────────────────────────

    private void OnEulaToggled(object sender, RoutedEventArgs e)
    {
        if (_filling || !_canManage) return;
        var accepted = EulaCheck.IsChecked == true;
        App.State.Settings.SetServerEulaAccepted(_pack.Id, accepted);
        App.State.Settings.Save();

        // The server reads this file, so un-ticking writes eula=false rather than just deleting it.
        try
        {
            var path = Path.Combine(_runDir, "eula.txt");
            if (accepted)
            {
                if (Directory.Exists(_runDir)) File.WriteAllText(path, "eula=true\n");
            }
            else if (File.Exists(path)) File.WriteAllText(path, "eula=false\n");
        }
        catch (Exception ex) { AppLog.LogError("server-host-ui", ex); }

        AppLog.Log("server", $"Minecraft EULA {(accepted ? "accepted" : "withdrawn")} for '{_pack.Name}'.");
        FillEula();
        Paint();
    }

    private void OnOpenEula(object sender, RoutedEventArgs e)
    {
        if (!SafeLaunch.OpenUrl(LaunchService.ServerEulaNotAcceptedException.EulaUrl))
            SetStatus("The EULA page could not be opened. It is at "
                    + LaunchService.ServerEulaNotAcceptedException.EulaUrl + ".");
    }

    // ── console ──────────────────────────────────────────────────────────────

    private void OnConsoleScrolled(object sender, ScrollChangedEventArgs e)
    {
        // Only user scrolls count. Appending a line raises this too, with the extent changing, and
        // treating that as a scroll would stop the console following.
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) return;
        var atBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 2;
        if (atBottom == _follow) return;
        _follow = atBottom;
        FollowToggle.IsChecked = atBottom;
    }

    private void OnFollowToggled(object sender, RoutedEventArgs e)
    {
        _follow = FollowToggle.IsChecked == true;
        if (_follow) ConsoleLog.ScrollToEnd();
    }

    private void OnCopyConsole(object sender, RoutedEventArgs e)
    {
        var text = ConsoleLog.Text;
        SetStatus(ClipboardHelper.TrySetText(text)
            ? "The console was copied to the clipboard."
            : "The clipboard is held by another program, so nothing was copied.");
    }

    private void OnClearConsole(object sender, RoutedEventArgs e)
    {
        ConsoleLog.Clear();
        Host.ClearConsole(_pack.Id);
        SetStatus("The console view was emptied. The server's own log file in server-run/logs is untouched.");
    }

    private async void OnQuickCommand(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string command }) await RunCommandAsync(command);
    }

    private async void OnSendCommand(object sender, RoutedEventArgs e) => await SendTypedAsync();

    private async void OnCommandKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await SendTypedAsync();
                break;

            // The index sits one past the end while nothing is recalled, so the first Up gets the newest
            // command and Down walks back out to an empty box, like a shell.
            case Key.Up when _history.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Max(0, _historyIndex - 1);
                SetCommandText(_history[_historyIndex]);
                break;

            case Key.Down when _history.Count > 0:
                e.Handled = true;
                _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                SetCommandText(_historyIndex >= _history.Count ? "" : _history[_historyIndex]);
                break;
        }
    }

    private void SetCommandText(string text)
    {
        CommandBox.Text = text;
        CommandBox.CaretIndex = text.Length;
    }

    private async Task SendTypedAsync()
    {
        var command = CommandBox.Text?.Trim() ?? "";
        if (command.Length == 0) return;
        CommandBox.Clear();
        await RunCommandAsync(command);
    }

    private async Task RunCommandAsync(string command)
    {
        try
        {
            if (!_canManage) return;
            if (Host.GetStatus(_pack.Id).Status != ServerStatus.Running)
            {
                SetStatus("The server is not running, so there is nothing to send the command to.");
                return;
            }

            // Server consoles take commands without the leading slash; a pasted "/list" would do nothing.
            var clean = command.TrimStart('/');
            Remember(command);
            var sent = await Host.SendCommandAsync(_pack.Id, clean);
            SetStatus(sent ? "" : "The server did not take that command - it may have just exited.");
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-ui", ex);
            SetStatus("That command could not be sent. The full error is in the launcher log.");
        }
    }

    private void Remember(string command)
    {
        if (_history.Count == 0 || !string.Equals(_history[^1], command, StringComparison.Ordinal))
        {
            _history.Add(command);
            while (_history.Count > MaxCommandHistory) _history.RemoveAt(0);
            CommandHistories[_pack.Id] = _history;
        }
        _historyIndex = _history.Count;
    }

    // ── memory / JVM / auto-restart ──────────────────────────────────────────

    private void OnRamChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_filling || !_canManage) return;
        App.State.Settings.SetServerMaxRamFor(_pack.Id, (int)RamSlider.Value);
        App.State.Settings.Save();
        PaintRamLabel();
        SetStatus($"The server gets {(int)RamSlider.Value:N0} MB the next time it starts.");
    }

    private void OnRamInherit(object sender, RoutedEventArgs e)
    {
        if (!_canManage) return;
        App.State.Settings.SetServerMaxRamFor(_pack.Id, null);
        App.State.Settings.Save();
        FillSettings();
        SetStatus("The server follows the instance's own memory setting again.");
    }

    private void OnJvmArgsCommitted(object sender, RoutedEventArgs e)
    {
        if (_filling || !_canManage) return;
        var settings = App.State.Settings;
        var next = JvmArgsBox.Text?.Trim() ?? "";
        if (next == settings.GetServerJvmArgsFor(_pack.Id)) return;
        settings.SetServerJvmArgsFor(_pack.Id, next);
        settings.Save();
        SetStatus(next.Length == 0
            ? "The server runs with the stock flags again."
            : "Those arguments are added to the stock flags the next time the server starts.");
    }

    private void OnAutoRestartToggled(object sender, RoutedEventArgs e)
    {
        if (_filling || !_canManage) return;
        var on = AutoRestartCheck.IsChecked == true;
        App.State.Settings.SetServerAutoRestart(_pack.Id, on);
        App.State.Settings.Save();
        SetStatus(on
            ? "A crash brings the server back automatically - five tries with a growing delay, then it stays down."
            : "A crash leaves the server down.");
    }

    // ── access lists ─────────────────────────────────────────────────────────

    private ServerAccessList SelectedList => (ServerAccessList)Math.Clamp(ListKindBox.SelectedIndex, 0, 3);

    private void OnListKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        PaintListKindChrome();
        _ = LoadAccessListAsync();
    }

    /// <summary>The add row's wording for the list that is showing. Also called on load, so the
    /// first list doesn't show the XAML placeholder label.</summary>
    private void PaintListKindChrome()
    {
        var bans = SelectedList is ServerAccessList.BannedPlayers or ServerAccessList.BannedIps;
        EntryReasonBox.Visibility = bans ? Visibility.Visible : Visibility.Collapsed;
        AddEntryLabel.Text = SelectedList switch
        {
            ServerAccessList.Ops => "Make an operator",
            ServerAccessList.Whitelist => "Add to the whitelist",
            ServerAccessList.BannedPlayers => "Ban",
            _ => "Ban this IP"
        };
        EntryNameBox.ToolTip = SelectedList == ServerAccessList.BannedIps
            ? "An IP address, e.g. 203.0.113.5"
            : "A player name";
    }

    private async Task LoadAccessListAsync()
    {
        var kind = SelectedList;
        _playersState.Begin("Reading the server's " + NameOf(kind) + ".");
        try
        {
            var dir = _runDir;
            // Read the whitelist switch in the same background hop as the list.
            var (rows, whitelistOn) = await Task.Run(() =>
                (ReadRows(kind, dir), Directory.Exists(dir) && ServerPropertiesService.Read(dir).Properties.WhiteList));
            if (kind != SelectedList) return; // the user moved on while it was being read

            EntryList.ItemsSource = rows;
            _playersState.EmptyCopy(EmptyTitleFor(kind), EmptyBodyFor(kind));
            _playersState.Content(rows.Count, countText: $"{rows.Count:N0} on the {NameOf(kind)}");

            _filling = true;
            try { WhitelistOnCheck.IsChecked = whitelistOn; }
            finally { _filling = false; }
        }
        catch (Exception ex)
        {
            _playersState.Error("The server's " + NameOf(kind) + " could not be read.", ex);
        }
    }

    private static string NameOf(ServerAccessList kind) => kind switch
    {
        ServerAccessList.Ops => "operator list",
        ServerAccessList.Whitelist => "whitelist",
        ServerAccessList.BannedPlayers => "banned-player list",
        _ => "banned-IP list"
    };

    private string EmptyTitleFor(ServerAccessList kind) => kind switch
    {
        ServerAccessList.Ops => "Nobody is an operator",
        ServerAccessList.Whitelist => "The whitelist is empty",
        ServerAccessList.BannedPlayers => "Nobody is banned",
        _ => "No IP is banned"
    };

    private string EmptyBodyFor(ServerAccessList kind)
    {
        if (!Directory.Exists(_runDir))
            return "This server has not been installed yet, so it has no lists. Install it first.";
        return kind switch
        {
            ServerAccessList.Ops => "Add yourself by name so you can run commands in game.",
            ServerAccessList.Whitelist => "With the whitelist on and nobody on it, nobody can join. "
                                        + "Add the players who may.",
            _ => "Nothing to show, which is the good case."
        };
    }

    private static List<ServerAccessRow> ReadRows(ServerAccessList kind, string dir)
    {
        if (!Directory.Exists(dir)) return new List<ServerAccessRow>();
        return kind switch
        {
            ServerAccessList.Ops => ServerAccessListService.ReadOps(dir).Select(o => new ServerAccessRow
            {
                Name = o.Name,
                Detail = $"level {o.Level}" + (o.Uuid.Length > 0 ? " · " + o.Uuid : " · no uuid on file"),
                RemoveHint = $"Take operator away from {o.Name}"
            }).ToList(),

            ServerAccessList.Whitelist => ServerAccessListService.ReadWhitelist(dir).Select(w => new ServerAccessRow
            {
                Name = w.Name,
                Detail = w.Uuid.Length > 0 ? w.Uuid : "no uuid on file",
                RemoveHint = $"Take {w.Name} off the whitelist"
            }).ToList(),

            ServerAccessList.BannedPlayers => ServerAccessListService.ReadBannedPlayers(dir).Select(b => new ServerAccessRow
            {
                Name = b.Name,
                Detail = $"{b.Reason} · banned {BanTime(b.Created)} by {b.Source} · expires {BanTime(b.Expires)}",
                RemoveHint = $"Unban {b.Name}"
            }).ToList(),

            _ => ServerAccessListService.ReadBannedIps(dir).Select(b => new ServerAccessRow
            {
                Name = b.Ip,
                Detail = $"{b.Reason} · banned {BanTime(b.Created)} by {b.Source} · expires {BanTime(b.Expires)}",
                RemoveHint = $"Unban {b.Ip}"
            }).ToList()
        };
    }

    /// <summary>Renders one of Minecraft's own ban timestamps in the reader's date order.</summary>
    /// <remarks>
    /// The game writes <c>2026-09-22 00:07:13 +0200</c> into <c>banned-players.json</c> and
    /// <c>banned-ips.json</c>. The parse is exact and invariant because that format is fixed; <c>zzz</c>
    /// accepts both <c>+0200</c> and <c>+02:00</c>. Anything else, such as "forever" on a permanent ban,
    /// is shown as is. The write side in <c>ServerAccessListService</c> stays invariant because the
    /// game reads it back.
    /// </remarks>
    private static string BanTime(string raw) =>
        DateTimeOffset.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss zzz",
                                     CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? TimeFormat.DateTime(at)
            : raw;

    private async void OnEntryBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await AddEntryAsync();
    }

    private async void OnAddEntry(object sender, RoutedEventArgs e) => await AddEntryAsync();

    private async Task AddEntryAsync()
    {
        try
        {
            if (!_canManage) return;
            var name = EntryNameBox.Text?.Trim() ?? "";
            if (name.Length == 0)
            {
                SetStatus(SelectedList == ServerAccessList.BannedIps
                    ? "Type an IP address first."
                    : "Type a player name first.");
                return;
            }

            var reason = EntryReasonBox.Text?.Trim() ?? "";
            var kind = SelectedList;
            var running = Host.GetStatus(_pack.Id).Status == ServerStatus.Running;

            if (running)
            {
                // A running server keeps these lists in memory and overwrites the file on stop, so use the
                // command instead of editing the file.
                var command = kind switch
                {
                    ServerAccessList.Ops => ServerAccessCommands.Op(name),
                    ServerAccessList.Whitelist => ServerAccessCommands.WhitelistAdd(name),
                    ServerAccessList.BannedPlayers => ServerAccessCommands.Ban(name, reason),
                    _ => ServerAccessCommands.BanIp(name, reason)
                };
                var sent = await Host.SendCommandAsync(_pack.Id, command);
                SetStatus(sent
                    ? $"Sent '{command}' to the running server."
                    : "The server did not take that command - it may have just exited.");
            }
            else
            {
                if (!Directory.Exists(_runDir))
                {
                    SetStatus("This server has not been installed yet, so it has no lists to add to.");
                    return;
                }

                // Best effort with a short timeout: adding a name must work offline.
                Guid? uuid = null;
                if (kind != ServerAccessList.BannedIps)
                {
                    using var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    uuid = await ServerAccessListService.LookupUuidAsync(name, lookup.Token);
                }

                var dir = _runDir;
                await Task.Run(() =>
                {
                    switch (kind)
                    {
                        case ServerAccessList.Ops: ServerAccessListService.AddOp(dir, name, uuid); break;
                        case ServerAccessList.Whitelist: ServerAccessListService.AddToWhitelist(dir, name, uuid); break;
                        case ServerAccessList.BannedPlayers:
                            ServerAccessListService.BanPlayer(dir, name, uuid, reason.Length > 0 ? reason : null);
                            break;
                        default:
                            ServerAccessListService.BanIp(dir, name, reason.Length > 0 ? reason : null);
                            break;
                    }
                });

                var file = kind switch
                {
                    ServerAccessList.Ops => ServerAccessListService.OpsFileName,
                    ServerAccessList.Whitelist => ServerAccessListService.WhitelistFileName,
                    ServerAccessList.BannedPlayers => ServerAccessListService.BannedPlayersFileName,
                    _ => ServerAccessListService.BannedIpsFileName
                };
                SetStatus($"Wrote {name} into {file}. The server reads it the next time it starts."
                        + (uuid is null && kind != ServerAccessList.BannedIps
                            ? " No uuid could be looked up, which is fine for an offline-mode server."
                            : ""));
            }

            EntryNameBox.Clear();
            EntryReasonBox.Clear();
            await LoadAccessListAsync();
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-ui", ex);
            SetStatus("That could not be added. The full error is in the launcher log.");
        }
    }

    private async void OnRemoveEntry(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_canManage) return;
            if (sender is not FrameworkElement { Tag: string name } || name.Length == 0) return;

            var kind = SelectedList;
            var (title, body, confirm) = kind switch
            {
                ServerAccessList.Ops => ("Take operator away",
                    $"{name} loses every operator command on this server, including the ones they are "
                    + "in the middle of using.", "Take it away"),
                ServerAccessList.Whitelist => ("Take off the whitelist",
                    $"{name} can no longer join, and is kicked immediately if 'Kick players removed "
                    + "from the whitelist' is on.", "Take them off"),
                ServerAccessList.BannedPlayers => ("Unban this player",
                    $"{name} can join again, and the reason they were banned for is removed from the "
                    + "ban list.", "Unban"),
                _ => ("Unban this IP",
                    $"Anyone on {name} can join again, and the reason it was banned for is removed "
                    + "from the ban list.", "Unban")
            };

            if (!await AppDialog.ConfirmAsync(_shell, title, body, confirm, "Cancel", danger: true)) return;

            var running = Host.GetStatus(_pack.Id).Status == ServerStatus.Running;
            if (running)
            {
                var command = kind switch
                {
                    ServerAccessList.Ops => ServerAccessCommands.Deop(name),
                    ServerAccessList.Whitelist => ServerAccessCommands.WhitelistRemove(name),
                    ServerAccessList.BannedPlayers => ServerAccessCommands.Pardon(name),
                    _ => ServerAccessCommands.PardonIp(name)
                };
                var sent = await Host.SendCommandAsync(_pack.Id, command);
                SetStatus(sent
                    ? $"Sent '{command}' to the running server."
                    : "The server did not take that command - it may have just exited.");
            }
            else
            {
                var dir = _runDir;
                await Task.Run(() =>
                {
                    switch (kind)
                    {
                        case ServerAccessList.Ops: ServerAccessListService.RemoveOp(dir, name); break;
                        case ServerAccessList.Whitelist: ServerAccessListService.RemoveFromWhitelist(dir, name); break;
                        case ServerAccessList.BannedPlayers: ServerAccessListService.PardonPlayer(dir, name); break;
                        default: ServerAccessListService.PardonIp(dir, name); break;
                    }
                });
                SetStatus($"Took {name} out of the {NameOf(kind)} on disk.");
            }

            await LoadAccessListAsync();
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-ui", ex);
            SetStatus("That could not be removed. The full error is in the launcher log.");
        }
    }

    private async void OnWhitelistToggled(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_filling || !_canManage) return;
            var on = WhitelistOnCheck.IsChecked == true;

            if (Host.GetStatus(_pack.Id).Status == ServerStatus.Running)
            {
                var command = on ? ServerAccessCommands.WhitelistOn() : ServerAccessCommands.WhitelistOff();
                await Host.SendCommandAsync(_pack.Id, command);
                SetStatus($"Sent '{command}' to the running server, and wrote white-list={on.ToString().ToLowerInvariant()} "
                        + "so it survives a restart.");
            }
            else
            {
                SetStatus($"white-list={on.ToString().ToLowerInvariant()} written to server.properties.");
            }

            var dir = _runDir;
            await Task.Run(() =>
            {
                var file = ServerPropertiesService.Read(dir);
                file.Properties.WhiteList = on;
                if (file.Properties.IsDirty) ServerPropertiesService.Save(file);
            });

            // The Server settings form is now out of date; re-read it unless it has unsaved edits.
            _props.ReloadIfClean();
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-ui", ex);
            SetStatus("The whitelist switch could not be written. The full error is in the launcher log.");
        }
    }

    // ── mirror preview ───────────────────────────────────────────────────────

    private void OnRecheckMirror(object sender, RoutedEventArgs e)
    {
        _clientOnlyRead = false;
        _ = RefreshMirrorAsync(CancellationToken.None);
    }

    private async Task RefreshMirrorAsync(CancellationToken ct)
    {
        // Never a placeholder zero: an em dash until a real count arrives.
        foreach (var slot in new[] { MirrorLinkedValue, MirrorCopiedValue, MirrorRemovedValue, MirrorSkippedValue })
            slot.Text = "-";
        MirrorSubtitle.Text = "Working out what a start would link, copy and leave behind...";
        ClientOnlyHeading.Text = "";
        ClientOnlyList.ItemsSource = null;
        ClientOnlyBox.Visibility = Visibility.Collapsed;
        MirrorRecheckButton.IsEnabled = false;

        try
        {
            var clientOnly = await ClientOnlyJarsAsync(ct);
            var preview = await Task.Run(
                () => App.State.Launcher.PreviewServerMirror(_pack, clientOnly, ct), ct);
            if (ct.IsCancellationRequested) return;

            var s = preview.Stats;
            MirrorLinkedValue.Text = s.Linked.ToString("N0");
            MirrorCopiedValue.Text = s.Copied.ToString("N0");
            MirrorRemovedValue.Text = s.Removed.ToString("N0");
            MirrorSkippedValue.Text = s.Skipped.ToString("N0");

            MirrorSubtitle.Text = s.NothingToDo
                ? "The server folder is already in step with this instance."
                : "This is what Install or Start would do to server-run right now. Jars are hard-linked, "
                + "so they cost no disk.";

            ClientOnlyList.ItemsSource = preview.ClientOnlyJars;
            ClientOnlyBox.Visibility = preview.ClientOnlyJars.Count == 0
                ? Visibility.Collapsed : Visibility.Visible;
            ClientOnlyHeading.Text = preview.ClientOnlyJars.Count == 0
                ? "No mod is marked 'Client only' in Modpack Management, so every enabled jar goes to the server."
                : $"{preview.ClientOnlyJars.Count:N0} mod(s) marked 'Client only' in Modpack Management stay off "
                + "the server - this is the answer to 'why is my minimap missing on the server'.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-ui", ex);
            MirrorSubtitle.Text = "What would go to the server could not be worked out. The full error "
                                + "is in the launcher log.";
        }
        finally { MirrorRecheckButton.IsEnabled = true; }
    }

    private async Task<IReadOnlyCollection<string>?> ClientOnlyJarsAsync(CancellationToken ct)
    {
        if (_clientOnlyRead) return _clientOnlyJars;
        try
        {
            var mods = await App.State.ModInventory.LoadAsync(_pack.Id, _pack.IsShared, ct);
            _clientOnlyJars = mods.Where(m => m.Side == ModSide.Client).Select(m => m.FileName).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Side flags unreadable: fall back to mirroring every enabled mod, and let the page say so.
            AppLog.LogError("server-host-ui", ex);
            _clientOnlyJars = null;
        }
        _clientOnlyRead = true;
        return _clientOnlyJars;
    }

    private void OnInnerTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // An inner ComboBox's selection bubbles up as this same event.
        if (e.OriginalSource != InnerTabs) return;
        if (_follow && InnerTabs.SelectedIndex == 0) ConsoleLog.ScrollToEnd();
    }

    // ── network hints ────────────────────────────────────────────────────────

    private void OnCopyAddress(object sender, RoutedEventArgs e)
    {
        var port = Host.GetStatus(_pack.Id).Port;
        if (port <= 0) port = _port;
        var address = $"{_lanAddress ?? "localhost"}:{port}";
        SetStatus(ClipboardHelper.TrySetText(address)
            ? $"Copied {address}."
            : "The clipboard is held by another program, so nothing was copied.");
    }

    /// <summary>
    /// This machine's address on the local network, or null when it has none.
    /// </summary>
    /// <remarks>Asks a UDP socket which local address it would route from. Nothing is sent, so it works
    /// offline. Enumerating adapters instead often finds a virtual switch's address first.</remarks>
    private static string? LocalIPv4()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("192.168.1.1"), 9));
            if (probe.LocalEndPoint is IPEndPoint { Address: { } local }
                && !IPAddress.IsLoopback(local) && local.ToString() != "0.0.0.0")
                return local.ToString();
        }
        catch (Exception ex) { AppLog.Log("server", "Could not work out this PC's LAN address: " + ex.Message); }

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork
                                     && !IPAddress.IsLoopback(a)
                                     && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                ?.ToString();
        }
        catch (Exception ex) { AppLog.LogError("server-host-ui", ex); return null; }
    }

    /// <summary>Asks the server who is on it. A loopback ping, so it works offline and notices a
    /// server that has stopped answering.</summary>
    private async Task PingAsync(int port)
    {
        try
        {
            var result = await MinecraftServerPing.PingAsync("127.0.0.1", port, TimeSpan.FromSeconds(2),
                                                             CancellationToken.None);
            _players = result.Online ? (result.PlayersOnline, result.PlayersMax) : null;
            PaintStatusDetail(Host.GetStatus(_pack.Id));
        }
        catch (Exception ex)
        {
            AppLog.Log("server", "Could not ping the local server: " + ex.Message);
            _players = null;
        }
    }
}
