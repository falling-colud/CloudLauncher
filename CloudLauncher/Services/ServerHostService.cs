using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>Where a pack's dedicated server is in its life.</summary>
public enum ServerStatus
{
    /// <summary>Not running. The initial state, and the one after a clean stop.</summary>
    Stopped = 0,

    /// <summary>Mirroring the pack and installing the loader server. Can take minutes the first time.</summary>
    Installing = 1,

    /// <summary>The JVM is up but the server hasn't finished loading. Can take minutes on a big modded
    /// pack.</summary>
    Starting = 2,

    /// <summary>The server printed its "Done" line: it is accepting players.</summary>
    Running = 3,

    /// <summary>A stop was asked for and the server is saving the world.</summary>
    Stopping = 4,

    /// <summary>The server exited on its own with a non-zero code. Separate from Stopped so the console
    /// and last lines stay on screen.</summary>
    Crashed = 5
}

/// <summary>The severity a server log line announces about itself.</summary>
public enum ServerLogLevel { Info = 0, Warn = 1, Error = 2, Debug = 3, Command = 4 }

/// <summary>One line of server console output (or a command that was sent to it).</summary>
public sealed record ServerLogLine(DateTimeOffset At, string Text, ServerLogLevel Level);

/// <summary>A snapshot of one pack's server, safe to hand to the UI thread and hold.</summary>
public sealed record ServerState
{
    public required Guid PackId { get; init; }
    public string PackName { get; init; } = "";
    public ServerStatus Status { get; init; } = ServerStatus.Stopped;
    public int? ProcessId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>The exit code of the last run, or null if it has not exited yet this session.</summary>
    public int? LastExitCode { get; init; }

    /// <summary>The port from the server's <c>server.properties</c>. 25565 until the file has been
    /// read.</summary>
    public int Port { get; init; } = 25565;

    /// <summary>How many times auto-restart has brought this server back since it last ran properly.</summary>
    public int RestartCount { get; init; }

    /// <summary>Something short to put on a status line: why it failed, or what it is doing.</summary>
    public string? Message { get; init; }

    /// <summary>The console tail from a crash, shown without opening the log.</summary>
    public IReadOnlyList<string> LastLines { get; init; } = Array.Empty<string>();

    /// <summary>What the last install resolved: loader, mirror counts, Java. Null before the first
    /// install of this session.</summary>
    public LaunchService.ServerInstallState? Install { get; init; }

    /// <summary>True while the launcher owns a process (or is about to). What a Stop button binds to.</summary>
    public bool IsLive => Status is ServerStatus.Installing or ServerStatus.Starting
        or ServerStatus.Running or ServerStatus.Stopping;

    /// <summary>How long the server has been up, or null when it isn't. <see cref="StartedAt"/> keeps
    /// the last run's start time after a stop, so it isn't an uptime on its own.</summary>
    public TimeSpan? Uptime =>
        StartedAt is { } at && Status is ServerStatus.Starting or ServerStatus.Running or ServerStatus.Stopping
            ? DateTimeOffset.UtcNow - at
            : null;
}

/// <summary>Owns the dedicated-server processes the launcher starts: one per pack, with its console,
/// its status, and the way to stop it.</summary>
/// <remarks>
/// <para>Like <see cref="MinecraftInstanceService"/> for clients, but a server is driven through its
/// streams: stdout is its only status and stdin the only clean way to stop it.</para>
/// <para>Redirected output has to be drained or the process blocks on a full pipe, so every server
/// is read for its whole life into a bounded ring (5000 lines, like <see cref="AppLog"/>).</para>
/// <para>Events are raised on the UI thread like <see cref="ProgressHub"/>, with output lines
/// batched: modded startup prints hundreds of lines a second.</para>
/// </remarks>
public sealed class ServerHostService(AppSettings settings, PackFolderService packs, LaunchService launcher)
{
    /// <summary>Console scrollback kept per server.</summary>
    private const int MaxConsoleLines = 5000;

    /// <summary>How long a graceful stop gets before the process tree is killed. Saving a big modded
    /// world can take this long, and killing it mid-save corrupts chunks.</summary>
    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Auto-restart gives up after this many tries. A pack that crashes on boot would
    /// otherwise restart all night, filling the disk with crash reports.</summary>
    private const int MaxAutoRestarts = 5;

    /// <summary>Uptime that counts as healthy and refills the restart budget.</summary>
    private static readonly TimeSpan HealthyUptime = TimeSpan.FromMinutes(10);

    /// <summary>"Done (12.345s)! For help, type "help"". Every Minecraft server since 1.7 prints this
    /// when it starts accepting players.</summary>
    private static readonly Regex DoneLine = new(@"Done\s*\(([0-9.,]+)s\)!", RegexOptions.Compiled);

    /// <summary>The level in a log prefix: <c>[12:00:00] [Server thread/INFO]</c>, or the older
    /// <c>[12:00:00 INFO]:</c>. Only the start of the line is searched, so the word ERROR inside a
    /// message does not repaint the whole line red.</summary>
    private static readonly Regex LevelPrefix =
        new(@"\[(?:[^\]]*?[/\s])?(FATAL|ERROR|WARN(?:ING)?|INFO|DEBUG|TRACE)\]", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, ServerInstance> _servers = new();

    /// <summary>Raised when a server's status changes. UI thread.</summary>
    public event Action<Guid>? StateChanged;

    /// <summary>Raised for each console line, inside the batched flush. UI thread.</summary>
    public event Action<Guid, ServerLogLine>? LineReceived;

    /// <summary>The whole batch at once, for a console view that would rather append in one go.
    /// Raised immediately before the per-line events for the same batch. UI thread.</summary>
    public event Action<Guid, IReadOnlyList<ServerLogLine>>? LinesReceived;

    // ── status ───────────────────────────────────────────────────────────────

    public ServerState GetStatus(Guid packId)
    {
        lock (_gate)
            return _servers.TryGetValue(packId, out var inst)
                ? inst.Snapshot()
                : new ServerState { PackId = packId };
    }

    /// <summary>True while this pack's server is installing, starting, running or stopping, i.e. while
    /// Start must not be offered.</summary>
    public bool IsBusy(Guid packId) => GetStatus(packId).IsLive;

    public bool IsRunning(Guid packId) => GetStatus(packId).Status == ServerStatus.Running;

    /// <summary>Every server this launcher currently owns.</summary>
    /// <remarks>Checked on shutdown: closing the launcher shouldn't leave a server running unnoticed or
    /// kill one people are playing on, so the user decides.</remarks>
    public IReadOnlyList<ServerState> ActiveServers()
    {
        lock (_gate)
            return _servers.Values.Where(i => i.Snapshot().IsLive).Select(i => i.Snapshot()).ToList();
    }

    /// <summary>The console scrollback for a pack, oldest first.</summary>
    public IReadOnlyList<ServerLogLine> GetConsole(Guid packId)
    {
        lock (_gate)
            return _servers.TryGetValue(packId, out var inst)
                ? inst.Console.ToList()
                : Array.Empty<ServerLogLine>();
    }

    public void ClearConsole(Guid packId)
    {
        lock (_gate)
            if (_servers.TryGetValue(packId, out var inst)) inst.Console.Clear();
    }

    // ── start / stop ─────────────────────────────────────────────────────────

    /// <summary>
    /// Installs if needed, then starts the pack's server and tracks it.
    /// </summary>
    /// <exception cref="InvalidOperationException">This pack already has a live server.</exception>
    /// <exception cref="LaunchService.ServerEulaNotAcceptedException">The Minecraft EULA has not been
    /// accepted for this pack. Thrown before any work is done, so the UI can ask and retry.</exception>
    public async Task<ServerState> StartAsync(
        PackDetail pack,
        IProgress<string>? log = null,
        IReadOnlyCollection<string>? clientOnlyJarNames = null,
        CancellationToken ct = default)
    {
        ServerInstance inst;
        lock (_gate)
        {
            _servers.TryGetValue(pack.Id, out var previous);
            if (previous is not null && previous.Snapshot().IsLive)
                throw new InvalidOperationException($"'{pack.Name}' already has a server running.");
            inst = new ServerInstance(pack.Id, pack.Name);
            if (previous is not null)
            {
                // Keep the scrollback across a restart; it explains the crash.
                foreach (var line in previous.Console) inst.Console.Enqueue(line);
                inst.RestartCount = previous.RestartCount;
            }
            _servers[pack.Id] = inst;
        }

        return await StartCoreAsync(inst, pack, log, clientOnlyJarNames, ct);
    }

    private async Task<ServerState> StartCoreAsync(
        ServerInstance inst,
        PackDetail pack,
        IProgress<string>? log,
        IReadOnlyCollection<string>? clientOnlyJarNames,
        CancellationToken ct)
    {
        inst.ClientOnlyJars = clientOnlyJarNames;
        inst.StopRequested = false;
        inst.LastExitCode = null;
        // Stop during the install has nothing to kill, so it cancels this token instead.
        var installToken = inst.BeginStart(ct);
        SetStatus(inst, ServerStatus.Installing, "Preparing the server...");

        // Show the install log in the console too, so the mirror and installer progress are visible.
        var bridge = new Progress<string>(line =>
        {
            log?.Report(line);
            Append(inst, new ServerLogLine(DateTimeOffset.Now, line, ServerLogLevel.Info));
        });

        LaunchService.ServerLaunchPlan plan;
        try
        {
            plan = await launcher.PrepareServerLaunchAsync(pack, bridge, clientOnlyJarNames, installToken);
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-host", ex);
            SetStatus(inst, ServerStatus.Stopped, ex.Message);
            throw;
        }

        inst.Install = plan.Install;
        inst.Port = plan.Port;

        var process = new Process { StartInfo = plan.StartInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Buffer(inst, e.Data, isError: false);
        process.ErrorDataReceived += (_, e) => Buffer(inst, e.Data, isError: true);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-host", ex);
            inst.StopFlushTimer();
            SetStatus(inst, ServerStatus.Stopped, "The server process did not start: " + ex.Message);
            throw;
        }

        inst.Process = process;
        inst.StartedAt = DateTimeOffset.UtcNow;
        inst.ReachedRunningAt = null;
        inst.StartFlushTimer(() => FlushLines(inst));
        SetStatus(inst, ServerStatus.Starting, $"Starting on port {inst.Port}...");
        AppLog.Log("server-host", $"'{pack.Name}' server started (PID {process.Id}) on port {inst.Port}.");

        // Watch for the exit off the Exited event: the blocking WaitForExit() afterwards is what makes
        // the async readers deliver their last lines before the exit is reported.
        _ = Task.Run(async () =>
        {
            try { await process.WaitForExitAsync(); } catch { /* already gone */ }
            try { process.WaitForExit(); } catch { }
            OnExited(inst, pack, process);
        });

        return inst.Snapshot();
    }

    /// <summary>Stops the pack's server: <c>stop</c> on stdin, then kills the whole process tree if it
    /// hasn't exited by <paramref name="timeout"/>.</summary>
    /// <param name="force">Kill immediately. Only for a hung server, since it interrupts the world
    /// save.</param>
    public async Task StopAsync(Guid packId, bool force = false, TimeSpan? timeout = null)
    {
        ServerInstance? inst;
        lock (_gate) _servers.TryGetValue(packId, out inst);
        if (inst is null) return;

        inst.StopRequested = true;
        var process = inst.Process;

        if (process is null)
        {
            // Still installing: cancelling the install is the stop.
            inst.Cancel();
            SetStatus(inst, ServerStatus.Stopped, "Cancelled.");
            return;
        }

        SetStatus(inst, ServerStatus.Stopping, force ? "Killing the server..." : "Stopping the server...");

        if (!force)
        {
            await SendCommandAsync(packId, "stop");
            try
            {
                using var cts = new CancellationTokenSource(timeout ?? DefaultStopTimeout);
                await process.WaitForExitAsync(cts.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                Append(inst, new ServerLogLine(DateTimeOffset.Now,
                    "The server did not stop in time; killing it.", ServerLogLevel.Warn));
            }
            catch (Exception ex) { AppLog.LogError("server-host", ex); }
        }

        try
        {
            if (!HasExited(process)) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppLog.LogError("server-host-kill", ex);
            throw new InvalidOperationException("Could not stop the server: " + ex.Message, ex);
        }
    }

    /// <summary>Stops it if it is up, then starts it again. Used by the Restart button and by
    /// auto-restart.</summary>
    public async Task<ServerState> RestartAsync(
        PackDetail pack,
        IProgress<string>? log = null,
        IReadOnlyCollection<string>? clientOnlyJarNames = null,
        CancellationToken ct = default)
    {
        if (IsBusy(pack.Id)) await StopAsync(pack.Id);
        return await StartAsync(pack, log, clientOnlyJarNames, ct);
    }

    /// <summary>Stops every live server, for app shutdown. Failures are logged, not thrown, so one
    /// wedged server can't stop the launcher from closing.</summary>
    public async Task StopAllAsync(bool force = false, TimeSpan? timeout = null)
    {
        List<Guid> ids;
        lock (_gate) ids = _servers.Where(kv => kv.Value.Snapshot().IsLive).Select(kv => kv.Key).ToList();
        foreach (var id in ids)
        {
            try { await StopAsync(id, force, timeout); }
            catch (Exception ex) { AppLog.LogError("server-host-stopall", ex); }
        }
    }

    // ── console ──────────────────────────────────────────────────────────────

    /// <summary>Sends a command to the running server's stdin, as if typed into its console.</summary>
    /// <remarks>Also the way to op, whitelist or ban while the server is up (see
    /// <see cref="ServerAccessCommands"/>): a running server keeps those lists in memory and overwrites
    /// the files on stop.</remarks>
    /// <returns>False when there is no server to send to, or the pipe has already closed.</returns>
    public async Task<bool> SendCommandAsync(Guid packId, string command, CancellationToken ct = default)
    {
        ServerInstance? inst;
        lock (_gate) _servers.TryGetValue(packId, out inst);
        var process = inst?.Process;
        if (inst is null || process is null || HasExited(process)) return false;

        // One call is one command: a line break inside it would reach the console as a second
        // command nobody typed.
        var text = (command ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length == 0) return false;

        await inst.StdinGate.WaitAsync(ct);
        try
        {
            await process.StandardInput.WriteLineAsync(text.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            Append(inst, new ServerLogLine(DateTimeOffset.Now, "> " + text, ServerLogLevel.Command));
            return true;
        }
        catch (Exception ex)
        {
            // The server exited or closed its stdin. No exception; the UI's status already says why.
            AppLog.Log("server-host", $"Could not send '{text}': {ex.Message}");
            return false;
        }
        finally { inst.StdinGate.Release(); }
    }

    /// <summary>Reads the level a server log line declares for itself.</summary>
    public static ServerLogLevel LevelOf(string line)
    {
        var head = line.Length > 96 ? line[..96] : line;
        var m = LevelPrefix.Match(head);
        if (!m.Success) return ServerLogLevel.Info;
        return m.Groups[1].Value.ToUpperInvariant() switch
        {
            "FATAL" or "ERROR" => ServerLogLevel.Error,
            "WARN" or "WARNING" => ServerLogLevel.Warn,
            "DEBUG" or "TRACE" => ServerLogLevel.Debug,
            _ => ServerLogLevel.Info
        };
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private void Buffer(ServerInstance inst, string? data, bool isError)
    {
        if (data is null) return;
        var text = data.TrimEnd();
        if (text.Length == 0) return;
        var level = isError ? ServerLogLevel.Error : LevelOf(text);
        inst.Pending.Enqueue(new ServerLogLine(DateTimeOffset.Now, text, level));
    }

    /// <summary>Moves the buffered lines into the ring and out to subscribers, once per tick.</summary>
    private void FlushLines(ServerInstance inst)
    {
        if (inst.Pending.IsEmpty) return;
        var batch = new List<ServerLogLine>();
        while (inst.Pending.TryDequeue(out var line)) batch.Add(line);

        var reachedDone = false;
        lock (_gate)
        {
            foreach (var line in batch)
            {
                inst.Console.Enqueue(line);
                while (inst.Console.Count > MaxConsoleLines) inst.Console.Dequeue();
                if (!reachedDone && DoneLine.IsMatch(line.Text)) reachedDone = true;
            }
        }

        Raise(() =>
        {
            LinesReceived?.Invoke(inst.PackId, batch);
            foreach (var line in batch) LineReceived?.Invoke(inst.PackId, line);
        });

        if (reachedDone && inst.Status == ServerStatus.Starting)
        {
            inst.ReachedRunningAt = DateTimeOffset.UtcNow;
            SetStatus(inst, ServerStatus.Running, $"Running on port {inst.Port}.");
        }
    }

    /// <summary>Appends a line the launcher wrote itself (install progress, a sent command, a note).</summary>
    private void Append(ServerInstance inst, ServerLogLine line)
    {
        lock (_gate)
        {
            inst.Console.Enqueue(line);
            while (inst.Console.Count > MaxConsoleLines) inst.Console.Dequeue();
        }
        Raise(() =>
        {
            LinesReceived?.Invoke(inst.PackId, new[] { line });
            LineReceived?.Invoke(inst.PackId, line);
        });
    }

    private void OnExited(ServerInstance inst, PackDetail pack, Process process)
    {
        int? exitCode = null;
        try { exitCode = process.ExitCode; } catch { }

        inst.StopFlushTimer();
        FlushLines(inst);

        var ranLongEnough = inst.ReachedRunningAt is { } at && DateTimeOffset.UtcNow - at >= HealthyUptime;
        if (ranLongEnough) inst.RestartCount = 0;

        inst.Process = null;
        inst.LastExitCode = exitCode;
        var crashed = !inst.StopRequested && exitCode is not 0;

        if (crashed)
        {
            SetStatus(inst, ServerStatus.Crashed,
                $"The server exited with code {exitCode?.ToString() ?? "?"}.");
            AppLog.Log("server-host", $"'{pack.Name}' server crashed (exit {exitCode}).");
        }
        else
        {
            SetStatus(inst, ServerStatus.Stopped,
                inst.StopRequested ? "Stopped." : "The server shut itself down.");
            AppLog.Log("server-host", $"'{pack.Name}' server stopped (exit {exitCode}).");
        }

        try { process.Dispose(); } catch { }

        if (crashed && settings.GetServerAutoRestart(pack.Id))
            ScheduleAutoRestart(inst, pack);
    }

    /// <summary>Brings a crashed server back, with a backoff and a hard cap.</summary>
    /// <remarks>A pack that crashes on boot crashes the same way every time, so after five tries
    /// (backoff doubling from 5 seconds to a minute) it stays down with the reason on screen.</remarks>
    private void ScheduleAutoRestart(ServerInstance inst, PackDetail pack)
    {
        if (inst.RestartCount >= MaxAutoRestarts)
        {
            Append(inst, new ServerLogLine(DateTimeOffset.Now,
                $"Auto-restart gave up after {MaxAutoRestarts} attempts. Fix the crash above and start it again.",
                ServerLogLevel.Error));
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, inst.RestartCount)));
        inst.RestartCount++;
        Append(inst, new ServerLogLine(DateTimeOffset.Now,
            $"Auto-restart {inst.RestartCount}/{MaxAutoRestarts} in {delay.TotalSeconds:0}s...", ServerLogLevel.Warn));

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                // The user may have started it, or chosen to leave it stopped, in the meantime.
                lock (_gate)
                {
                    if (!_servers.TryGetValue(pack.Id, out var current) || !ReferenceEquals(current, inst)) return;
                    if (inst.StopRequested || inst.Status != ServerStatus.Crashed) return;
                }
                await StartCoreAsync(inst, pack, null, inst.ClientOnlyJars, CancellationToken.None);
            }
            catch (Exception ex) { AppLog.LogError("server-host-restart", ex); }
        });
    }

    private void SetStatus(ServerInstance inst, ServerStatus status, string? message)
    {
        lock (_gate)
        {
            inst.Status = status;
            inst.Message = message;
            inst.LastLines = status == ServerStatus.Crashed
                ? inst.Console.TakeLast(20).Select(l => l.Text).ToList()
                : Array.Empty<string>();
        }
        Raise(() => StateChanged?.Invoke(inst.PackId));
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    /// <summary>Same dispatcher hop as <see cref="ProgressHub"/>. Without a WPF application (a test
    /// host) the handler runs inline.</summary>
    private static void Raise(Action a)
    {
        var app = Application.Current;
        if (app is null) { a(); return; }
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.BeginInvoke(a);
    }

    /// <summary>The server folder for <paramref name="packId"/>, so a hosting page can reach
    /// server.properties and the access lists through this service.</summary>
    public string ServerRunDir(Guid packId) => packs.ServerRunDir(packId);

    // ── per-pack state ───────────────────────────────────────────────────────

    private sealed class ServerInstance(Guid packId, string packName)
    {
        public Guid PackId { get; } = packId;
        public string PackName { get; } = packName;

        public Process? Process;
        public ServerStatus Status = ServerStatus.Stopped;
        public string? Message;
        public DateTimeOffset? StartedAt;

        /// <summary>When the server last printed its Done line, for the "ran long enough to count as
        /// healthy" test that refills the restart budget.</summary>
        public DateTimeOffset? ReachedRunningAt;

        public int? LastExitCode;
        public int Port = 25565;
        public int RestartCount;
        public bool StopRequested;
        public IReadOnlyList<string> LastLines = Array.Empty<string>();
        public LaunchService.ServerInstallState? Install;
        public IReadOnlyCollection<string>? ClientOnlyJars;

        public readonly Queue<ServerLogLine> Console = new();
        public readonly ConcurrentQueue<ServerLogLine> Pending = new();
        public readonly SemaphoreSlim StdinGate = new(1, 1);

        private CancellationTokenSource? _cts;
        private System.Timers.Timer? _flush;

        /// <summary>Opens the cancellation scope for one install+start, linked to the caller's token.</summary>
        public CancellationToken BeginStart(CancellationToken external)
        {
            try { _cts?.Dispose(); } catch { }
            _cts = external.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(external)
                : new CancellationTokenSource();
            return _cts.Token;
        }

        public void Cancel() { try { _cts?.Cancel(); } catch { } }

        public void StartFlushTimer(Action flush)
        {
            StopFlushTimer();
            _flush = new System.Timers.Timer(150) { AutoReset = true };
            _flush.Elapsed += (_, _) => flush();
            _flush.Start();
        }

        public void StopFlushTimer()
        {
            var timer = _flush;
            _flush = null;
            try { timer?.Stop(); timer?.Dispose(); } catch { }
        }

        public ServerState Snapshot() => new()
        {
            PackId = PackId,
            PackName = PackName,
            Status = Status,
            ProcessId = SafePid(),
            StartedAt = StartedAt,
            LastExitCode = LastExitCode,
            Port = Port,
            RestartCount = RestartCount,
            Message = Message,
            LastLines = LastLines,
            Install = Install
        };

        private int? SafePid()
        {
            try { return Process?.Id; } catch { return null; }
        }
    }
}
