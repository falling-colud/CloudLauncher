using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CloudLauncher.Shared;
using CmlLib.Core;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;

namespace CloudLauncher.Services;

public sealed class LaunchService(
    AppSettings settings,
    MinecraftAccountService accounts,
    PackFolderService packs,
    MinecraftInstanceService instances)
{
    private static readonly HttpClient Http = ApiClient.WithUserAgent(new() { Timeout = TimeSpan.FromMinutes(10) });

    // Set via a setter to break a construction cycle: the defaults engine depends on services that
    // AppState builds after this one. Optional; when null, launches skip shared defaults (as in tests).
    private ContentDefaultsService? _contentDefaults;

    /// <summary>Gives the launch path the shared-content defaults engine. See
    /// <see cref="ContentDefaultsService.ReconcileForLaunchAsync"/> for why it is called twice.</summary>
    public void SetContentDefaults(ContentDefaultsService defaults) => _contentDefaults = defaults;

    /// <param name="joinServerAddress">Optional <c>host</c> or <c>host:port</c> to connect to as soon
    /// as the game reaches the main menu. See <see cref="LaunchAsync"/>.</param>
    public async Task<Process> LaunchTrackedAsync(
        PackDetail pack,
        IProgress<string>? log,
        CancellationToken ct = default,
        string? joinServerAddress = null)
    {
        using var launch = instances.BeginLaunch(pack.Id, ct);
        var process = await LaunchAsync(pack, log, launch.Token, joinServerAddress);
        var launchToken = launch.Token;
        if (!launch.Complete(process))
            throw new OperationCanceledException("Launch was cancelled.", launchToken);

        return process;
    }

    /// <param name="joinServerAddress">
    /// Optional <c>host</c> or <c>host:port</c> to connect to instead of stopping at the main menu
    /// (the Servers page's "Join").
    /// </param>
    /// <remarks>
    /// Passed to CmlLib as <see cref="MLaunchOption.ServerIp"/>/<c>ServerPort</c>, which emits
    /// <c>--quickPlayMultiplayer</c> where supported and <c>--server</c>/<c>--port</c> otherwise.
    /// </remarks>
    public async Task<Process> LaunchAsync(
        PackDetail pack,
        IProgress<string>? log,
        CancellationToken ct = default,
        string? joinServerAddress = null)
    {
        return await Task.Run(() => LaunchCoreAsync(pack, log, ct, joinServerAddress), ct);
    }

    private async Task<Process> LaunchCoreAsync(
        PackDetail pack,
        IProgress<string>? log,
        CancellationToken ct = default,
        string? joinServerAddress = null)
    {
        void Report(string m) { log?.Report(m); AppLog.Log("launch", m); }

        if (pack.IsEmpty)
            throw new InvalidOperationException("This pack is marked as empty - it can't launch Minecraft.");
        if (string.IsNullOrEmpty(pack.MinecraftVersion))
            throw new InvalidOperationException("Pack has no Minecraft version set.");
        EnsurePlainVersions(pack);

        Report($"Launching pack '{pack.Name}' ({pack.MinecraftVersion}, {pack.Loader})");
        ProgressHub.Indeterminate(pack.Id, "Preparing...");

        // Libraries, assets and version JARs go into the shared runtime directory, so they are
        // downloaded once and reused across all packs.
        var runtimeDir = AppSettings.RuntimeRoot;
        Directory.CreateDirectory(runtimeDir);
        var gameDir = packs.GameDir(pack.Id);
        Directory.CreateDirectory(gameDir);
        // Shared defaults, step one of three. This places files into local/, so it must run before
        // the overlay copies local/ into game/. The overlay never overwrites, so an instance's own
        // files still win over defaults.
        if (_contentDefaults is { } defaults)
        {
            try
            {
                var reconciled = await defaults.ReconcileForLaunchAsync(pack, log, ct);
                if (reconciled.DidAnything || reconciled.Refused)
                    Report("Shared defaults: " + reconciled.Summary());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A default that cannot be placed never blocks the launch; the instance is
                // complete without it.
                AppLog.LogError("content-defaults", ex);
                Report("Shared defaults could not be applied: " + ex.Message);
            }
        }
        // Overlay local/ files into game/ (local/ holds per-user additions not synced to the
        // server). Shared files are synced straight into game/, so they need no overlay.
        if (Directory.Exists(packs.LocalDir(pack.Id)))
            Report("Overlaying local/ into game/ for launch...");
        packs.PrepareLaunchOverlay(pack.Id);
        // Step three: activate the defaults. It writes game/options.txt and the shader config, so
        // it runs after the overlay (the files it names must exist in game/) and before
        // LowModeService.Apply, whose options.txt write must come last.
        if (_contentDefaults is { } activator)
        {
            try
            {
                var switched = await activator.ActivateForLaunchAsync(pack, log, ct);
                if (switched.DidAnything || switched.Refused)
                    Report("Shared defaults: " + switched.Summary());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.LogError("content-defaults", ex);
                Report("Shared defaults could not be switched on: " + ex.Message);
            }
        }
        // Forcing windowed + no-pause-on-lost-focus only exists so the game can be
        // embedded in the custom host window. When that's disabled, leave the pack's
        // options.txt alone so fullscreen and pause behave as Minecraft normally would.
        if (settings.UseCustomGameWindow)
        {
            OptionsTxtService.EnsureWindowed(gameDir);
            OptionsTxtService.EnsureNoPauseOnLostFocus(gameDir);
        }
        // Low mode lowers heavy visual settings and disables some decorative client-only mods (or
        // restores them when off). Applied here so it covers files the sync just pulled down.
        // Those mods are not on the server and send no payloads, so low-mode players can still
        // join and share packs.
        // Only the packs LowModeService.AppliesTo lists offer low mode; other packs get `false`, which
        // restores packs that older builds lowered by default.
        Report(LowModeService.Apply(gameDir,
            LowModeService.AppliesTo(pack, settings) && LowModeService.IsEnabled(settings, pack.Id)));
        // MCEF's ~270 MB of natives are downloaded per player and never synced, so an interrupted
        // download leaves a folder that MCEF considers installed and will not retry. The client
        // then dies during init with UnsatisfiedLinkError. Clear a broken bundle so it refetches.
        var mcefMsg = McefLibrariesService.Verify(gameDir);
        if (!string.IsNullOrEmpty(mcefMsg)) Report(mcefMsg);
        var path = CreateMinecraftPath(runtimeDir, gameDir);
        Report($"Runtime: {runtimeDir}");
        Report($"Game dir: {gameDir}");

        var launcher = new MinecraftLauncher(path);
        var overallFraction = -1.0;
        var overallLabel = "Installing files...";
        var currentResourceLabel = "";
        var lastReportedTask = -1;
        var lastLoggedTask = -1;
        var lastByteReportTicks = 0L;
        const int byteReportIntervalMs = 150;
        launcher.FileProgressChanged += (_, e) =>
        {
            if (e.TotalTasks <= 0) return;

            // CmlLib can fire many events per file; only advance UI/log when a task completes.
            if (e.ProgressedTasks == lastReportedTask) return;
            lastReportedTask = e.ProgressedTasks;

            overallFraction = Math.Min(0.95, (double)e.ProgressedTasks / e.TotalTasks);
            overallLabel = $"{e.EventType} {e.ProgressedTasks}/{e.TotalTasks}";
            currentResourceLabel = e.Name;
            ProgressHub.Report(pack.Id, overallFraction, overallLabel, 0, currentResourceLabel);

            if (e.ProgressedTasks != lastLoggedTask)
            {
                lastLoggedTask = e.ProgressedTasks;
                var msg = $"{e.EventType}: {e.Name} ({e.ProgressedTasks}/{e.TotalTasks})";
                AppLog.Log("launch", msg);
                log?.Report(msg);
            }
        };
        launcher.ByteProgressChanged += (_, e) =>
        {
            if (e.TotalBytes <= 0) return;
            var now = Environment.TickCount64;
            var complete = e.ProgressedBytes >= e.TotalBytes;
            if (!complete && now - lastByteReportTicks < byteReportIntervalMs) return;
            lastByteReportTicks = now;

            var frac = (double)e.ProgressedBytes / e.TotalBytes;
            var label = overallFraction >= 0 ? overallLabel : $"Downloading... {(int)(frac * 100)}%";
            ProgressHub.Report(pack.Id,
                overallFraction,
                label,
                frac,
                string.IsNullOrWhiteSpace(currentResourceLabel) ? "Current resource" : currentResourceLabel);
        };

        Report("Resolving Minecraft version...");
        var versionId = await ResolveVersionAsync(launcher, pack, log, ct);
        Report($"Version: {versionId}");

        Report("Resolving Minecraft account...");
        ProgressHub.Indeterminate(pack.Id, "Signing in...");
        MSession session;
        try
        {
            session = await accounts.GetLaunchSessionAsync(ct);
        }
        catch (Exception ex)
        {
            AppLog.LogError("account", ex);
            ProgressHub.Clear(pack.Id);
            throw new InvalidOperationException(
                "Could not resolve a Minecraft account. Sign in (or pick) an account from the title-bar chip.", ex);
        }
        Report($"Account: {session.Username} ({(string.IsNullOrEmpty(session.AccessToken) ? "offline" : "online")})");

        ProgressHub.Indeterminate(pack.Id, "Installing files...");
        Report($"Installing/checking files for {versionId}...");

        JavaSelection java;
        try
        {
            // Bootstrap the game with the Java the Minecraft version itself needs
            // (e.g. Forge 1.12.2's launchwrapper only works on Java 8). If the pack
            // ships the ReLauncher mod it relaunches into its own target JVM afterwards.
            java = await EnsureJavaExecutableAsync(pack.MinecraftVersion, pack.Id, settings.GetJavaPathFor(pack.Id), Report, ct);
        }
        catch
        {
            ProgressHub.Clear(pack.Id);
            throw;
        }
        Report($"Selected Java: {java.Path} ({java.Message})");

        // Keep config/relauncher.json's javaPath valid and pointed at the JVM the pack's ReLauncher
        // mod relaunches into (its targetJavaVersion, e.g. Java 25), not the Java we bootstrap with.
        await SyncRelauncherJavaAsync(gameDir, java, pack.Id, Report, ct);

        var maxRam = settings.GetMaxRamFor(pack.Id);
        var minRam = Math.Min(1024, maxRam);
        var args = new MLaunchOption
        {
            Session       = session,
            Path          = path,
            JavaPath      = java.Path,
            MinimumRamMb  = minRam,
            MaximumRamMb  = maxRam,
            VersionType   = "CloudLauncher",
            GameLauncherName = "CloudLauncher",
            GameLauncherVersion = "1"
        };
        // Join a server on launch. CmlLib turns these into --quickPlayMultiplayer or
        // --server/--port depending on the Minecraft version.
        if (!string.IsNullOrWhiteSpace(joinServerAddress))
        {
            var (joinHost, joinPort) = MinecraftServerPing.ParseAddress(joinServerAddress);
            // The address can come from a synced servers.dat and ends up on the command line, so it
            // must be a plain host token the game cannot read as an option.
            if (!IsPlainHost(joinHost)) joinHost = "";
            if (joinHost.Length > 0)
            {
                args.ServerIp = joinHost;
                args.ServerPort = joinPort;
                Report($"Will connect to {joinHost}:{joinPort} on startup.");
            }
            else
            {
                Report($"Could not read '{joinServerAddress}' as a server address - starting at the main menu.");
            }
        }

        var extraJvm = BuildExtraJvmArguments(settings.GetJvmArgsFor(pack.Id));
        if (extraJvm is not null)
        {
            args.ExtraJvmArguments = extraJvm;
            Report($"Extra JVM args: {settings.GetJvmArgsFor(pack.Id)}");
        }

        Process process;
        System.Timers.Timer? flushTimer = null;
        try
        {
            process = await launcher.InstallAndBuildProcessAsync(versionId, args);
            flushTimer = ConfigureProcessLogging(process, gameDir, Report);
        }
        catch (Exception ex)
        {
            flushTimer?.Dispose();
            AppLog.LogError("install", ex);
            ProgressHub.Clear(pack.Id);
            throw;
        }

        Report("Files ready. Starting Minecraft...");
        ProgressHub.Report(pack.Id, 1.0, "Starting Minecraft...");
        var startedAt = DateTimeOffset.UtcNow;
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            settings.AddPackPlayTime(pack.Id, DateTimeOffset.UtcNow - startedAt);
        };
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            settings.RecordPackLaunchStarted(pack.Id);
        }
        catch (Exception ex)
        {
            // The game never started, so the Exited handler that normally disposes the flush timer
            // will not fire. Dispose it here to avoid leaking the timer and log sink.
            flushTimer?.Stop();
            flushTimer?.Dispose();
            AppLog.LogError("process.Start", ex);
            ProgressHub.Clear(pack.Id);
            throw;
        }
        Report($"Minecraft started (PID {process.Id}).");
        ProgressHub.Clear(pack.Id);
        return process;
    }

    // ── dedicated server ──────────────────────────────────────────────────────

    /// <summary>What a mirror pass did, or (in a preview) would do.</summary>
    public sealed record ServerMirrorStats(int Linked, int Copied, int Removed, int Skipped, int ClientOnlyMods)
    {
        public static readonly ServerMirrorStats Empty = new(0, 0, 0, 0, 0);

        /// <summary>True when the server directory is already in step with the pack: nothing to
        /// link, copy or remove.</summary>
        public bool NothingToDo => Linked == 0 && Copied == 0 && Removed == 0;
    }

    /// <summary>A dry run of the mirror: what it would do, and which mods it would
    /// leave behind.</summary>
    public sealed record ServerMirrorPreview(ServerMirrorStats Stats, IReadOnlyList<string> ClientOnlyJars);

    /// <summary>Everything the install pass resolved (loader, mirror result, Java), so a hosting
    /// page can show the state of a server it has not started.</summary>
    public sealed record ServerInstallState(
        Guid PackId,
        string ServerRunDir,
        LoaderKind Loader,
        string? LoaderVersion,
        string MinecraftVersion,
        bool LoaderAlreadyInstalled,
        ServerMirrorStats Mirror,
        string JavaPath,
        int? JavaMajor,
        string JavaMessage,
        string LaunchArguments,
        bool EulaAccepted,
        int Port);

    /// <summary>An installed server plus the process to start, unstarted so the caller
    /// (<see cref="ServerHostService"/>) can wire up stdout/stderr/stdin before it runs.</summary>
    public sealed record ServerLaunchPlan(ServerInstallState Install, ProcessStartInfo StartInfo)
    {
        public int Port => Install.Port;
        public string ServerRunDir => Install.ServerRunDir;
    }

    /// <summary>
    /// The pack's server has not been allowed to accept the Minecraft EULA.
    /// </summary>
    /// <remarks>A separate type so the UI can offer the fix (a checkbox and a link to the
    /// agreement) instead of a generic error.</remarks>
    public sealed class ServerEulaNotAcceptedException(Guid packId)
        : InvalidOperationException(
            "You must accept the Minecraft EULA before this pack can run as a server. " +
            "Read it at " + EulaUrl + " and tick \"I accept the Minecraft EULA\" in the server settings.")
    {
        public const string EulaUrl = "https://aka.ms/MinecraftEULA";
        public Guid PackId { get; } = packId;
    }

    /// <summary>True when this pack's server may write <c>eula=true</c>: the user ticked the box, or an
    /// <c>eula.txt</c> they accepted themselves is already in the folder.</summary>
    public bool IsServerEulaAccepted(Guid packId) =>
        settings.GetServerEulaAccepted(packId) || EulaAcceptedOnDisk(packs.ServerRunDir(packId));

    /// <summary>Reads an existing <c>eula.txt</c>. An acceptance made outside the launcher still
    /// counts; the launcher just never accepts on the user's behalf.</summary>
    public static bool EulaAcceptedOnDisk(string serverRunDir)
    {
        try
        {
            var path = Path.Combine(serverRunDir, "eula.txt");
            if (!File.Exists(path)) return false;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (line[..eq].Trim().Equals("eula", StringComparison.OrdinalIgnoreCase))
                    return line[(eq + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* unreadable: treat as not accepted */ }
        return false;
    }

    /// <summary>
    /// True when this pack's loader server is already installed in <c>server-run/</c>, so a UI can
    /// offer "Install" or "Start" without touching the network.
    /// </summary>
    public bool IsServerInstalled(PackDetail pack)
    {
        if (string.IsNullOrEmpty(pack.MinecraftVersion)) return false;
        if (!HasPlainVersions(pack)) return false;
        var dir = packs.ServerRunDir(pack.Id);
        if (!Directory.Exists(dir)) return false;
        return pack.Loader switch
        {
            LoaderKind.Fabric => File.Exists(Path.Combine(dir, $"fabric-server-mc.{pack.MinecraftVersion}-loader.{pack.LoaderVersion}.jar")),
            LoaderKind.NeoForge or LoaderKind.Forge => ForgeLikeServerInstalled(pack, dir).Installed,
            _ => File.Exists(Path.Combine(dir, "server.jar")),
        };
    }

    /// <summary>
    /// Brings the pack's <c>server-run/</c> folder up to date and makes sure the right loader
    /// server is in it, without starting anything.
    /// </summary>
    /// <remarks>
    /// <para>Separate from starting because the first NeoForge install downloads hundreds of
    /// megabytes. Slow work runs off the UI thread and reports through <paramref name="log"/> and
    /// <see cref="ProgressHub"/>.</para>
    /// <para>The server folder mirrors game/ (see <see cref="MirrorForServer"/>) with the pack's own
    /// <c>server/</c> folder laid over it, and the server matches the pack's loader.</para>
    /// </remarks>
    public async Task<ServerInstallState> EnsureServerInstalledAsync(
        PackDetail pack,
        IProgress<string>? log = null,
        IReadOnlyCollection<string>? clientOnlyJarNames = null,
        CancellationToken ct = default)
    {
        if (pack.IsEmpty)
            throw new InvalidOperationException("Empty packs can't run as a server.");
        if (string.IsNullOrEmpty(pack.MinecraftVersion))
            throw new InvalidOperationException("Pack has no Minecraft version set.");
        EnsurePlainVersions(pack);

        void Report(string m) { log?.Report(m); AppLog.Log("server", m); }
        ProgressHub.Indeterminate(pack.Id, "Preparing server...");
        try
        {
            return await Task.Run(async () =>
            {
                var gameDir = packs.GameDir(pack.Id);
                var serverOverrideDir = packs.ServerOverrideDir(pack.Id);
                Directory.CreateDirectory(serverOverrideDir);
                var serverRunDir = packs.ServerRunDir(pack.Id);
                Directory.CreateDirectory(serverRunDir);

                // No shared-defaults reconcile here. A dedicated server reads neither options.txt nor
                // iris.properties, configs and KubeJS already arrive via the mirror from game/, and
                // instantiating a world template would create a save in the client's saves/ folder.
                if (Directory.Exists(packs.LocalDir(pack.Id)))
                {
                    Report("Overlaying local/ into game/ for server launch...");
                    packs.PrepareLaunchOverlay(pack.Id);
                }

                Report("Mirroring the pack into server-run/ (mods, configs, scripts)...");
                ProgressHub.Indeterminate(pack.Id, "Preparing server...", "Mirroring pack files");
                var clientOnly = new HashSet<string>(clientOnlyJarNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                var mirror = MirrorForServer(gameDir, serverRunDir, serverOverrideDir, settings, clientOnly, false, null, ct);
                Report($"Server files ready: {mirror.Linked} jar(s) linked, {mirror.Copied} file(s) copied, " +
                       $"{mirror.Removed} stale file(s) removed, {mirror.Skipped} client-side or private file(s) left out.");
                if (mirror.ClientOnlyMods > 0)
                    Report($"{mirror.ClientOnlyMods} mod(s) marked \"Client only\" in Modpack Management were left off the server.");
                CopyDirectory(serverOverrideDir, serverRunDir, overwrite: true);

                // Only write the EULA once the user has accepted it. Installed but not accepted is
                // a valid state; the gate is on Start.
                var eulaAccepted = settings.GetServerEulaAccepted(pack.Id) || EulaAcceptedOnDisk(serverRunDir);
                if (eulaAccepted) File.WriteAllText(Path.Combine(serverRunDir, "eula.txt"), "eula=true\n");

                ProgressHub.Indeterminate(pack.Id, "Preparing server...", "Checking Java");
                var java = await EnsureJavaExecutableAsync(
                    pack.MinecraftVersion, pack.Id, settings.GetJavaPathFor(pack.Id), Report, ct);
                Report($"Java: {java.Path} ({java.Message})");
                await SyncRelauncherJavaAsync(serverRunDir, java, pack.Id, Report, ct);

                var loader = await PrepareServerLauncherAsync(pack, serverRunDir, java, Report, ct);

                // The server writes server.properties on its first run. Create a starter file now
                // so the hosting page can show settings and a port before the first start.
                var props = ServerPropertiesService.ReadOrCreate(serverRunDir, motd: pack.Name);

                return new ServerInstallState(
                    pack.Id, serverRunDir, pack.Loader, pack.LoaderVersion, pack.MinecraftVersion!,
                    loader.AlreadyInstalled, mirror, java.Path, java.Major, java.Message,
                    loader.LaunchArguments, eulaAccepted, props.Properties.ServerPort);
            }, ct);
        }
        finally { ProgressHub.Clear(pack.Id); }
    }

    /// <summary>
    /// Installs the server if needed and returns the process to start, ready to have its streams
    /// wired up. Nothing is started here.
    /// </summary>
    /// <exception cref="ServerEulaNotAcceptedException">The user has not accepted the Minecraft
    /// EULA for this pack. Checked before installing so it fails fast.</exception>
    public async Task<ServerLaunchPlan> PrepareServerLaunchAsync(
        PackDetail pack,
        IProgress<string>? log = null,
        IReadOnlyCollection<string>? clientOnlyJarNames = null,
        CancellationToken ct = default)
    {
        if (!IsServerEulaAccepted(pack.Id)) throw new ServerEulaNotAcceptedException(pack.Id);

        var install = await EnsureServerInstalledAsync(pack, log, clientOnlyJarNames, ct);

        var maxRam = settings.GetServerMaxRamFor(pack.Id);
        var jvm = BuildServerJvmArguments(maxRam, settings.GetServerJvmArgsFor(pack.Id));
        var psi = new ProcessStartInfo
        {
            FileName = install.JavaPath,
            WorkingDirectory = install.ServerRunDir,
            Arguments = $"{jvm} {install.LaunchArguments} nogui",
            // Redirected rather than a console window, so the launcher can read the output, stop
            // the server cleanly and notice a crash.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };

        log?.Report($"Server command: java {RedactLaunchArguments(psi.Arguments)}");
        log?.Report($"Players connect on port {install.Port}.");
        return new ServerLaunchPlan(install, psi);
    }

    /// <summary>
    /// Starts a dedicated server for the pack and returns the running process, with its output
    /// forwarded to <paramref name="log"/>.
    /// </summary>
    /// <remarks>Untracked; anything that needs status, stop, console or commands should use
    /// <see cref="ServerHostService"/>. The streams must be drained, or the server fills its pipe
    /// buffer and hangs while loading mods.</remarks>
    public async Task<Process> StartLocalServerAsync(
        PackDetail pack,
        IProgress<string>? log,
        IReadOnlyCollection<string>? clientOnlyJarNames = null,
        CancellationToken ct = default)
    {
        var plan = await PrepareServerLaunchAsync(pack, log, clientOnlyJarNames, ct);
        var process = new Process { StartInfo = plan.StartInfo, EnableRaisingEvents = true };
        void Pump(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            log?.Report("[server] " + line.TrimEnd());
            AppLog.Log("server", line.TrimEnd());
        }
        process.OutputDataReceived += (_, e) => Pump(e.Data);
        process.ErrorDataReceived += (_, e) => Pump(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        AppLog.Log("server", $"Server started (PID {process.Id}) on port {plan.Port}.");
        return process;
    }

    /// <summary>
    /// What the mirror would do to this pack's server folder, and which mods it would leave behind,
    /// without writing anything. Used by the preview beside the Install button.
    /// </summary>
    public ServerMirrorPreview PreviewServerMirror(
        PackDetail pack,
        IReadOnlyCollection<string>? clientOnlyJarNames = null,
        CancellationToken ct = default)
        => PreviewServerMirror(
            packs.GameDir(pack.Id), packs.ServerRunDir(pack.Id), packs.ServerOverrideDir(pack.Id),
            settings, clientOnlyJarNames, ct);

    /// <summary>Directory-level dry run, for callers that already hold the paths (and
    /// for tests).</summary>
    public static ServerMirrorPreview PreviewServerMirror(
        string gameDir,
        string serverRunDir,
        string serverOverrideDir,
        AppSettings settings,
        IReadOnlyCollection<string>? clientOnlyJarNames = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(gameDir))
            return new ServerMirrorPreview(ServerMirrorStats.Empty, Array.Empty<string>());
        var clientOnly = new HashSet<string>(clientOnlyJarNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        var stats = MirrorForServer(gameDir, serverRunDir, serverOverrideDir, settings, clientOnly, true, found, ct);
        return new ServerMirrorPreview(stats, found);
    }

    /// <summary>
    /// The JVM flags a server starts with: its heap, the stock GC tuning, then the pack's
    /// own server arguments.
    /// </summary>
    /// <remarks>
    /// As with <see cref="BuildExtraJvmArguments"/>, extra arguments add to the stock flags rather
    /// than replace them. The stock GC block is dropped only when the user picks their own
    /// collector, since two <c>-XX:+Use...GC</c> flags stop the JVM from starting.
    /// </remarks>
    public static string BuildServerJvmArguments(int maxRamMb, string? userArgs)
    {
        var user = (userArgs ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var picksOwnCollector = user.Any(a =>
            a.StartsWith("-XX:+Use", StringComparison.OrdinalIgnoreCase) && a.EndsWith("GC", StringComparison.OrdinalIgnoreCase));

        var parts = new List<string> { $"-Xmx{maxRamMb}M", $"-Xms{Math.Min(1024, maxRamMb)}M" };
        if (!picksOwnCollector) parts.AddRange(StockJvmFlags);
        // Java 17 still takes its encoding from the Windows code page, which garbles any non-ASCII
        // text in the log (player names included).
        parts.Add("-Dfile.encoding=UTF-8");
        parts.AddRange(user);
        return string.Join(' ', parts);
    }

    // ── server directory mirror ───────────────────────────────────────────────

    /// <summary>Top-level game/ folders a dedicated server has no use for (or must not share with
    /// the client): worlds, purely client-side assets, caches, and the launcher's own
    /// bookkeeping.</summary>
    private static readonly HashSet<string> ServerSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "resourcepacks", "shaderpacks", "screenshots", "logs", "crash-reports", ".cloudlauncher",
        ".mixin.out", "downloads", "libraries", "versions", "assets", ".fabric", "local", "server-run",
        "xaero", "XaeroWorldMap", "XaeroWaypoints", "journeymap", "cachedImages", "replay_recordings",
        "schematics", "emotes", "skins", "CustomSkinLoader", "irisUpdateInfo", "modernfix", "flightpaths",
        ".voxy", ".bobby", "cache", ".cache", "webcache", "backups", "simplebackups", "texturepacks", "fancymenu_data",
        // The loader installers the launcher downloads live here; a synced copy must
        // never stand in for one.
        ".installers"
    };

    /// <summary>Folders inside <c>mods/</c> that are per-player client caches, not mods (MCEF's
    /// ~270 MB of Chromium natives, Connector's remapped-jar cache).</summary>
    private static readonly HashSet<string> ServerSkipModSubdirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "mcef-libraries", ".connector"
    };

    /// <summary>Root-level files that belong to the client session, not the server.</summary>
    private static readonly HashSet<string> ServerSkipFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "options.txt", "optionsof.txt", "optionsshaders.txt", "servers.dat", "servers.dat_old",
        "usercache.json", "usernamecache.json", "realms_persistence.json", "hotbar.nbt",
        "command_history.txt", ".lowmode-backup.json", "eula.txt",
        // What the server is started with belongs to this machine: the loader stamp that says an
        // install is done, and the files loader run scripts read JVM options from.
        ".cloudlauncher-server-loader", "user_jvm_args.txt", "run.bat", "run.sh"
    };

    /// <summary>Brings <paramref name="serverDir"/> in line with <paramref name="gameDir"/>: jars
    /// are hard-linked, other files copied when they differ, client-only and private paths skipped,
    /// and jars that left the pack (or were disabled) removed from the server's mods folder. Jars
    /// from the pack's own <c>server/</c> overlay are kept.</summary>
    /// <param name="analyseOnly">Count what would happen without touching anything. Both modes make
    /// the same decisions, so the preview is accurate.</param>
    /// <param name="clientOnlyFound">Collects the relative path of each client-only mod left out.</param>
    private static ServerMirrorStats MirrorForServer(
        string gameDir, string serverDir, string serverOverrideDir, AppSettings settings,
        IReadOnlySet<string> clientOnlyJars, bool analyseOnly, List<string>? clientOnlyFound, CancellationToken ct)
    {
        int linked = 0, copied = 0, removed = 0, skipped = 0, clientOnlyMods = 0;
        var expectedJars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Skipped top-level folders (worlds, caches, LOD data) are never enumerated.
        IEnumerable<string> Sources()
        {
            foreach (var f in Directory.EnumerateFiles(gameDir, "*", SearchOption.TopDirectoryOnly))
                yield return f;
            foreach (var dir in Directory.EnumerateDirectories(gameDir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(dir);
                if (ServerSkipDirs.Contains(name)) { skipped++; continue; }
                if (string.Equals(name, "mods", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                        yield return f;
                    foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (ServerSkipModSubdirs.Contains(Path.GetFileName(sub))) { skipped++; continue; }
                        foreach (var f in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                            yield return f;
                    }
                    continue;
                }
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    yield return f;
            }
        }

        foreach (var src in Sources())
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(gameDir, src);
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var top = parts[0];
            var name = Path.GetFileName(rel);
            var isRootFile = parts.Length == 1;
            // A jar at the top of server-run/ is the server itself (server.jar, the Fabric launcher, a
            // legacy Forge jar), which the launcher downloads; game/ never supplies one.
            if ((isRootFile && name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                || (isRootFile && ServerSkipFiles.Contains(name))
                || name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                || PrivateAssetPolicy.IsPrivate(rel, settings))
            {
                skipped++;
                continue;
            }
            // A mod marked "Client only" (shaders, minimaps, LOD renderers...) would crash or be
            // refused by a dedicated server. Matched anywhere under mods/, including subfolders
            // like mods/optional/.
            if (string.Equals(top, "mods", StringComparison.OrdinalIgnoreCase)
                && parts.Length >= 2 && clientOnlyJars.Contains(name))
            {
                clientOnlyMods++;
                clientOnlyFound?.Add(rel.Replace(Path.DirectorySeparatorChar, '/'));
                continue;
            }

            var dst = Path.Combine(serverDir, rel);
            var isJar = name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
            if (isJar && string.Equals(top, "mods", StringComparison.OrdinalIgnoreCase)) expectedJars.Add(dst);

            var srcInfo = new FileInfo(src);
            var dstInfo = new FileInfo(dst);
            // A hard link shares size and write time with its source, and a faithful copy keeps both,
            // so "same size and time" means "already mirrored" for either kind.
            if (dstInfo.Exists && dstInfo.Length == srcInfo.Length && dstInfo.LastWriteTimeUtc == srcInfo.LastWriteTimeUtc)
                continue;

            var wouldLink = isJar && OperatingSystem.IsWindows();
            if (analyseOnly)
            {
                if (wouldLink) linked++; else copied++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            if (dstInfo.Exists) File.Delete(dst);
            // Reuses PackFolderService's CreateHardLinkW wrapper; its Windows check is already
            // implied by wouldLink.
            if (wouldLink && PackFolderService.TryCreateHardLink(dst, src))
            {
                linked++;
            }
            else
            {
                File.Copy(src, dst, overwrite: true);
                copied++;
            }
        }

        // Jars from the server-side overlay are the user's server-only mods, so keep them.
        var overlayMods = Path.Combine(serverOverrideDir, "mods");
        if (Directory.Exists(overlayMods))
            foreach (var f in Directory.EnumerateFiles(overlayMods, "*.jar", SearchOption.TopDirectoryOnly))
                expectedJars.Add(Path.Combine(serverDir, "mods", Path.GetFileName(f)));

        // Remove mods the pack no longer has, has disabled or marks client-only, including
        // .jar.disabled leftovers from older launcher versions.
        var serverMods = Path.Combine(serverDir, "mods");
        if (Directory.Exists(serverMods))
        {
            foreach (var f in Directory.EnumerateFiles(serverMods, "*", SearchOption.TopDirectoryOnly).ToList())
            {
                var isModFile = f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                                || f.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);
                if (!isModFile || expectedJars.Contains(f)) continue;
                if (analyseOnly) { removed++; continue; }
                try { File.Delete(f); removed++; } catch { /* locked by a running server */ }
            }
        }
        return new ServerMirrorStats(linked, copied, removed, skipped, clientOnlyMods);
    }

    // ── loader-aware server launcher ──────────────────────────────────────────

    /// <summary>The launch arguments for an installed loader server, and whether it was already
    /// there (so a hosting page can tell "ready" from "just installed").</summary>
    private sealed record ServerLoaderInstall(string LaunchArguments, bool AlreadyInstalled);

    /// <summary>Makes sure the right server for the pack's loader is present in <paramref name="dir"/>
    /// and returns the Java arguments that launch it (everything between the JVM flags and
    /// <c>nogui</c>).</summary>
    private async Task<ServerLoaderInstall> PrepareServerLauncherAsync(
        PackDetail pack, string dir, JavaSelection java, Action<string> report, CancellationToken ct)
    {
        var mc = pack.MinecraftVersion!;
        switch (pack.Loader)
        {
            case LoaderKind.Fabric:
            {
                if (string.IsNullOrEmpty(pack.LoaderVersion))
                    throw new InvalidOperationException("Fabric loader version not set.");
                var jarName = $"fabric-server-mc.{mc}-loader.{pack.LoaderVersion}.jar";
                var jar = Path.Combine(dir, jarName);
                var hadFabric = File.Exists(jar);
                if (!hadFabric)
                {
                    report($"Fetching the Fabric {pack.LoaderVersion} server launcher...");
                    ProgressHub.Indeterminate(pack.Id, "Preparing server...", "Fabric server launcher");
                    var installer = await GetFabricInstallerVersionAsync(ct);
                    var url = $"https://meta.fabricmc.net/v2/versions/loader/{mc}/{pack.LoaderVersion}/{installer}/server/jar";
                    await DownloadToAsync(url, jar, ct);
                    // Remove launchers left over from older loader versions.
                    foreach (var old in Directory.EnumerateFiles(dir, "fabric-server-mc.*.jar", SearchOption.TopDirectoryOnly).ToList())
                        if (!string.Equals(old, jar, StringComparison.OrdinalIgnoreCase))
                            try { File.Delete(old); } catch { }
                }
                var hadVanilla = await EnsureVanillaServerJarAsync(mc, dir, report, ct);
                return new ServerLoaderInstall($"-jar \"{jarName}\"", hadFabric && hadVanilla);
            }

            case LoaderKind.NeoForge:
            case LoaderKind.Forge:
                return await EnsureForgeLikeServerAsync(pack, dir, java, report, ct);

            default:
            {
                var had = await EnsureVanillaServerJarAsync(mc, dir, report, ct);
                return new ServerLoaderInstall("-jar \"server.jar\"", had);
            }
        }
    }

    /// <summary>Returns true when the jar was already there, i.e. nothing was downloaded.</summary>
    private async Task<bool> EnsureVanillaServerJarAsync(string mc, string dir, Action<string> report, CancellationToken ct)
    {
        var serverJar = Path.Combine(dir, "server.jar");
        if (File.Exists(serverJar)) return true;
        report($"Downloading the vanilla server jar for {mc}...");
        await DownloadVanillaServerJarAsync(mc, serverJar, null, ct);
        return false;
    }

    /// <summary>Where a Forge-like server's pieces live for this pack, and whether they are
    /// all there.</summary>
    /// <remarks>Shared by the installer and <see cref="IsServerInstalled"/> so both test the same
    /// condition.</remarks>
    private static (bool Installed, bool StampMatched, string ArgsRelative, string ArgsPath, string? LegacyJar, string StampPath, string Stamp)
        ForgeLikeServerInstalled(PackDetail pack, string dir)
    {
        var isNeo = pack.Loader == LoaderKind.NeoForge;
        var mc = pack.MinecraftVersion!;
        var v = pack.LoaderVersion ?? "";
        var argsFile = OperatingSystem.IsWindows() ? "win_args.txt" : "unix_args.txt";
        var argsRel = isNeo
            ? $"libraries/net/neoforged/neoforge/{v}/{argsFile}"
            : $"libraries/net/minecraftforge/forge/{mc}-{v}/{argsFile}";
        var argsPath = Path.Combine(dir, argsRel.Replace('/', Path.DirectorySeparatorChar));
        var legacyJar = isNeo ? null : Path.Combine(dir, $"forge-{mc}-{v}.jar");
        var stampPath = Path.Combine(dir, ".cloudlauncher-server-loader");
        var stamp = $"{pack.Loader}|{mc}|{v}";
        var stampMatched = File.Exists(stampPath)
                           && string.Equals(SafeReadAllText(stampPath).Trim(), stamp, StringComparison.Ordinal);
        var installed = File.Exists(argsPath) || (legacyJar is not null && File.Exists(legacyJar));
        return (installed, stampMatched, argsRel, argsPath, legacyJar, stampPath, stamp);
    }

    private static string SafeReadAllText(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    private async Task<ServerLoaderInstall> EnsureForgeLikeServerAsync(
        PackDetail pack, string dir, JavaSelection java, Action<string> report, CancellationToken ct)
    {
        var isNeo = pack.Loader == LoaderKind.NeoForge;
        var mc = pack.MinecraftVersion!;
        var v = pack.LoaderVersion;
        if (string.IsNullOrEmpty(v))
            throw new InvalidOperationException($"{pack.Loader} loader version not set.");

        var (installedNow, stampOk, argsRel, argsPath, legacyJar, stampPath, stamp) = ForgeLikeServerInstalled(pack, dir);
        bool Installed() => File.Exists(argsPath) || (legacyJar is not null && File.Exists(legacyJar));
        var alreadyInstalled = stampOk && installedNow;

        if (!alreadyInstalled)
        {
            var installerUrl = isNeo
                ? $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{v}/neoforge-{v}-installer.jar"
                : $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mc}-{v}/forge-{mc}-{v}-installer.jar";
            var installersDir = Path.Combine(dir, ".installers");
            Directory.CreateDirectory(installersDir);
            var installerJar = Path.Combine(installersDir, Path.GetFileName(new Uri(installerUrl).LocalPath));
            if (!File.Exists(installerJar))
            {
                report($"Downloading the {pack.Loader} {v} server installer...");
                ProgressHub.Indeterminate(pack.Id, "Preparing server...", $"{pack.Loader} installer");
                await DownloadToAsync(installerUrl, installerJar, ct);
            }

            report($"Installing the {pack.Loader} {v} server into server-run/ - it downloads its libraries, so the first time takes a few minutes...");
            ProgressHub.Indeterminate(pack.Id, "Preparing server...", $"Installing {pack.Loader} {v}");
            // Forge's installer spells the switch --installServer; NeoForge's documents
            // --install-server (and its LegacyInstaller lineage accepts the Forge spelling as well).
            // Try the documented one first and fall back if the installer rejects the option.
            var switches = isNeo ? new[] { "--install-server", "--installServer" } : new[] { "--installServer", "--install-server" };
            var ok = false;
            foreach (var sw in switches)
            {
                var (exit, output) = await RunToolAsync(java.Path, $"-jar \"{installerJar}\" {sw} \"{dir}\"", dir, report, ct);
                if (exit == 0 && Installed()) { ok = true; break; }
                if (!output.Contains("not a recognized option", StringComparison.OrdinalIgnoreCase)
                    && !output.Contains("is not a recognized", StringComparison.OrdinalIgnoreCase)
                    && !output.Contains("Unrecognized option", StringComparison.OrdinalIgnoreCase))
                    break;
                report($"The installer did not accept {sw}; trying the other spelling.");
            }
            if (!ok)
                throw new InvalidOperationException(
                    $"The {pack.Loader} {v} server installer did not complete. Check the log above - it usually names the library it could not download.");
            File.WriteAllText(stampPath, stamp);
            report($"{pack.Loader} {v} server installed.");
        }

        var launchArgs = File.Exists(argsPath) ? $"@{argsRel}" : $"-jar \"{Path.GetFileName(legacyJar!)}\"";
        return new ServerLoaderInstall(launchArgs, alreadyInstalled);
    }

    /// <summary>Runs a console tool (an installer) with its output streamed into the log, and returns
    /// its exit code plus the captured output for diagnostics.</summary>
    private static async Task<(int ExitCode, string Output)> RunToolAsync(
        string fileName, string arguments, string workingDir, Action<string> report, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var captured = new StringBuilder();
        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (captured) captured.AppendLine(line);
            report("  [installer] " + line.TrimEnd());
        }
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        string output;
        lock (captured) output = captured.ToString();
        return (p.ExitCode, output);
    }

    private static async Task DownloadToAsync(string url, string destPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var part = destPath + ".part";
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using (var fs = File.Create(part))
                await resp.Content.CopyToAsync(fs, ct);
            File.Move(part, destPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            throw;
        }
    }

    /// <summary>The newest stable Fabric installer version, which the meta server wants in the server
    /// launcher URL.</summary>
    private static async Task<string> GetFabricInstallerVersionAsync(CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(
            await Http.GetStreamAsync("https://meta.fabricmc.net/v2/versions/installer", ct), cancellationToken: ct);
        string? first = null;
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var version = entry.TryGetProperty("version", out var ve) ? ve.GetString() : null;
            if (string.IsNullOrEmpty(version)) continue;
            first ??= version;
            if (entry.TryGetProperty("stable", out var st) && st.ValueKind == JsonValueKind.True) return version;
        }
        return first ?? throw new InvalidOperationException("Fabric's meta server listed no installer versions.");
    }

    // ---------- version resolution ----------

    private async Task<string> ResolveVersionAsync(MinecraftLauncher launcher, PackDetail pack, IProgress<string>? log, CancellationToken ct)
    {
        switch (pack.Loader)
        {
            case LoaderKind.None:
                return pack.MinecraftVersion!;

            case LoaderKind.Fabric:
            {
                var fabric = new FabricInstaller(Http);
                log?.Report("Installing Fabric loader...");
                return await fabric.Install(pack.MinecraftVersion!, pack.LoaderVersion!, launcher.MinecraftPath);
            }

            case LoaderKind.Forge:
            {
                var forge = new ForgeInstaller(launcher);
                if (string.IsNullOrEmpty(pack.LoaderVersion))
                    throw new InvalidOperationException("Forge loader version not set.");
                log?.Report("Installing Forge...");
                return await forge.Install(pack.MinecraftVersion!, pack.LoaderVersion!);
            }

            case LoaderKind.NeoForge:
            {
                var neoforge = new NeoForgeInstaller(launcher);
                if (string.IsNullOrEmpty(pack.LoaderVersion))
                    throw new InvalidOperationException("NeoForge loader version not set.");
                log?.Report("Installing NeoForge...");
                return await neoforge.Install(pack.MinecraftVersion!, pack.LoaderVersion!);
            }

            default:
                throw new NotSupportedException($"Loader {pack.Loader} not supported");
        }
    }

    // ---------- helpers ----------

    /// <summary>
    /// Refuses a pack whose Minecraft or loader version is not a plain version name.
    /// </summary>
    /// <remarks>Both come from the pack (another user's data, for a shared pack) and end up in file
    /// paths, the server's argument-file path and download URLs.</remarks>
    private static void EnsurePlainVersions(PackDetail pack)
    {
        if (!HasPlainVersions(pack))
            throw new InvalidOperationException(
                "This instance's Minecraft or loader version is not a valid version name, so it cannot be installed or launched.");
    }

    private static bool HasPlainVersions(PackDetail pack) =>
        IsPlainVersion(pack.MinecraftVersion)
        && (pack.Loader == LoaderKind.None || string.IsNullOrEmpty(pack.LoaderVersion) || IsPlainVersion(pack.LoaderVersion));

    private static bool IsPlainVersion(string? version) =>
        version is { Length: > 0 and <= 64 }
        && !version.Contains("..", StringComparison.Ordinal)
        && version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+' or ' ')
        && PathSafety.IsSafeFileName(version);

    /// <summary>One host name or IP literal: nothing the game's argument parser could take as
    /// an option.</summary>
    private static bool IsPlainHost(string host) =>
        host.Length is > 0 and <= 255
        && host[0] != '-'
        && host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '%');

    /// <summary>
    /// Turns the pack's extra-JVM-args string into the list CmlLib should use, or null for "none set".
    /// Setting <see cref="MLaunchOption.ExtraJvmArguments"/> replaces CmlLib's stock GC flags, so
    /// the stock set is included first; otherwise one -D property would drop the G1 tuning.
    /// </summary>
    private static MArgument[]? BuildExtraJvmArguments(string userArgs)
    {
        if (string.IsNullOrWhiteSpace(userArgs)) return null;

        var user = userArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return StockJvmFlags.Concat(user).Select(a => new MArgument(a)).ToArray();
    }

    /// <summary>CmlLib.Core 4.0.6 MinecraftProcessBuilder defaults, verbatim. The server does not
    /// go through CmlLib, so <see cref="BuildServerJvmArguments"/> uses these to match.</summary>
    private static readonly string[] StockJvmFlags =
    [
        "-XX:+UnlockExperimentalVMOptions",
        "-XX:+UseG1GC",
        "-XX:G1NewSizePercent=20",
        "-XX:G1ReservePercent=20",
        "-XX:MaxGCPauseMillis=50",
        "-XX:G1HeapRegionSize=16M",
        "-Xss1M",
    ];

    private static void CopyDirectory(string src, string dst, bool overwrite)
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dst);
        foreach (var sub in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, sub);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }

    private static async Task DownloadVanillaServerJarAsync(string mcVersion, string outPath, IProgress<string>? log, CancellationToken ct)
    {
        // Look up the version manifest to find the per-version JSON, which in turn has the server URL.
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await Http.GetStreamAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct), cancellationToken: ct);
        string? versionUrl = null;
        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (v.GetProperty("id").GetString() == mcVersion)
            {
                versionUrl = v.GetProperty("url").GetString();
                break;
            }
        }
        if (versionUrl is null)
            throw new InvalidOperationException($"Minecraft version {mcVersion} not found in Mojang manifest");

        using var verDoc = await System.Text.Json.JsonDocument.ParseAsync(
            await Http.GetStreamAsync(versionUrl, ct), cancellationToken: ct);
        if (!verDoc.RootElement.TryGetProperty("downloads", out var downloads) ||
            !downloads.TryGetProperty("server", out var srv) ||
            !srv.TryGetProperty("url", out var srvUrl))
            throw new InvalidOperationException($"No server jar published for {mcVersion}");

        var url = srvUrl.GetString()!;
        log?.Report($"Server jar URL: {url}");
        await using var fs = File.Create(outPath);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await resp.Content.CopyToAsync(fs, ct);
    }

    private sealed record JavaSelection(string Path, int? Major, string Message);

    /// <summary>A Java the launcher can see: on the PATH, in the usual vendor folders, or one it
    /// downloaded itself (<see cref="Managed"/>).</summary>
    public sealed record JavaInstall(string Path, int Major, bool Managed);

    private static IReadOnlyList<JavaInstall>? _detectedJava;
    private static readonly SemaphoreSlim DetectJavaLock = new(1, 1);

    /// <summary>Every Java installation the launcher can find, newest major first. Probing runs
    /// <c>java -version</c> per candidate, so the answer is cached for the session; pass
    /// <paramref name="refresh"/> after installing something.</summary>
    public static async Task<IReadOnlyList<JavaInstall>> DetectJavaInstallsAsync(bool refresh = false)
    {
        if (!refresh && _detectedJava is not null) return _detectedJava;
        await DetectJavaLock.WaitAsync();
        try
        {
            if (!refresh && _detectedJava is not null) return _detectedJava;
            var list = await Task.Run(() =>
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var result = new List<JavaInstall>();
                foreach (var candidate in EnumerateJavaCandidates())
                {
                    var full = ResolveExecutable(candidate);
                    if (full is null || !seen.Add(full)) continue;
                    var major = TryGetJavaMajor(full);
                    if (major is null) continue;
                    var managed = full.StartsWith(AppSettings.JavaRoot, StringComparison.OrdinalIgnoreCase);
                    result.Add(new JavaInstall(full, major.Value, managed));
                }
                // java.exe and javaw.exe in one bin folder are the same runtime; list it once (java.exe).
                return (IReadOnlyList<JavaInstall>)result
                    .GroupBy(j => Path.GetDirectoryName(j.Path) ?? j.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderBy(j => Path.GetFileName(j.Path).Equals("java.exe", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
                    .OrderByDescending(j => j.Major)
                    .ThenBy(j => j.Managed)
                    .ThenBy(j => j.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });
            _detectedJava = list;
            return list;
        }
        finally { DetectJavaLock.Release(); }
    }

    /// <summary>The Java major a Minecraft version normally runs on (8 / 17 / 21), for the
    /// pickers' labels.</summary>
    public static int RequiredJavaMajorFor(string? mcVersion) => RequiredJavaMajor(mcVersion);

    /// <summary>A bare "java.exe" candidate means "whatever is on the PATH"; resolve it so it can be
    /// compared, shown, and probed like any other path.</summary>
    private static string? ResolveExecutable(string candidate)
    {
        try
        {
            if (Path.IsPathRooted(candidate))
                return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = Path.Combine(dir.Trim(), candidate);
                if (File.Exists(p)) return Path.GetFullPath(p);
            }
        }
        catch { /* a malformed PATH entry */ }
        return null;
    }

    /// <summary>The Java to launch with: the user's choice for the pack (or the launcher default)
    /// when it exists and answers, else the automatic pick for the Minecraft version. A chosen Java
    /// of an unexpected major is still used, with a note in the log.</summary>
    private static async Task<JavaSelection> EnsureJavaExecutableAsync(
        string? mcVersion,
        Guid packId,
        string? overridePath,
        Action<string> report,
        CancellationToken ct)
    {
        var required = RequiredJavaMajor(mcVersion);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (!File.Exists(overridePath))
            {
                report($"The chosen Java ({overridePath}) no longer exists; picking one automatically.");
            }
            else
            {
                var major = await Task.Run(() => TryGetJavaMajor(overridePath), ct);
                if (major is null)
                {
                    report($"The chosen Java ({overridePath}) did not answer to -version; picking one automatically.");
                }
                else
                {
                    if (major != required)
                        report($"Note: the chosen Java is {major}, while Minecraft {mcVersion} normally runs on Java {required}. Using it as asked.");
                    return new JavaSelection(overridePath, major,
                        major == required
                            ? $"Java {major}, chosen in the instance's options"
                            : $"Java {major}, chosen in the instance's options (Minecraft {mcVersion} expects {required})");
                }
            }
        }
        return await EnsureJavaMajorAsync(required, packId, report, ct);
    }

    /// <summary>
    /// Keeps a pack's <c>config/relauncher.json</c> javaPath valid. When the ReLauncher mod is
    /// enabled and targets a different Java major than <paramref name="launchJava"/>, that JVM is
    /// provisioned and javaPath points at it; otherwise the path is repaired to the bootstrap Java.
    /// </summary>
    private static async Task SyncRelauncherJavaAsync(
        string gameDir,
        JavaSelection launchJava,
        Guid packId,
        Action<string> report,
        CancellationToken ct)
    {
        var info = RelauncherConfigService.ReadInfo(gameDir);
        if (!info.Exists)
            return;

        var relauncherJavaPath = launchJava.Path;

        if (info.Enabled && info.TargetJavaMajor is int target && target != launchJava.Major)
        {
            try
            {
                report($"relauncher.json targets Java {target}; provisioning it for the mod's relaunch.");
                var targetJava = await EnsureJavaMajorAsync(target, packId, report, ct);
                relauncherJavaPath = targetJava.Path;
            }
            catch (Exception ex)
            {
                AppLog.LogError("relauncher", ex);
                // Don't bail out: a bad javaPath makes ReLauncher fail with "missing the java machine".
                // Fall back to the bootstrap Java so the path is at least valid.
                report($"Could not provision Java {target} for relauncher.json; falling back to {launchJava.Path}.");
                relauncherJavaPath = launchJava.Path;
            }
        }

        RelauncherConfigService.FixJavaPath(gameDir, relauncherJavaPath, report);
    }

    private static async Task<JavaSelection> EnsureJavaMajorAsync(
        int required,
        Guid packId,
        Action<string> report,
        CancellationToken ct)
    {
        if (TryResolveJavaForMajor(required, out var installed, out _))
            return installed;

        report($"No compatible Java {required} found. Downloading Java {required}...");
        ProgressHub.Report(packId, 0, $"Downloading Java {required}...");
        var downloaded = await DownloadManagedJavaAsync(required, packId, report, ct);
        ProgressHub.Report(packId, 1, $"Java {required} ready");
        return downloaded;
    }

    private static bool TryResolveJavaExecutable(
        string? mcVersion,
        out JavaSelection match,
        out string diagnostic)
        => TryResolveJavaForMajor(RequiredJavaMajor(mcVersion), out match, out diagnostic);

    private static bool TryResolveJavaForMajor(
        int required,
        out JavaSelection match,
        out string diagnostic)
    {
        var detected = EnumerateJavaCandidates()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new JavaSelection(path, TryGetJavaMajor(path), ""))
            .Where(j => j.Major is not null)
            .ToList();

        // Require the same major; a newer JDK is not a safe substitute:
        //   - Forge 1.20.1 ships ASM that can't read Java 25 classes ("Unsupported class
        //     file major version 69").
        //   - Forge 1.12.2 needs Java 8's URLClassLoader.
        // So a higher Java is never reused just because it is installed (e.g. one provisioned for
        // ReLauncher); resolve, and if needed download, the major the version needs.
        var found = detected.FirstOrDefault(j => j.Major == required);

        if (found is not null)
        {
            match = found with { Message = $"Java {found.Major} matches required Java {required}" };
            diagnostic = "";
            return true;
        }

        diagnostic = detected.Count == 0
            ? "no Java installations were detected"
            : "found " + string.Join(", ", detected.Select(j => $"Java {j.Major} at {j.Path}"));
        match = new JavaSelection("", null, diagnostic);
        return false;
    }

    private static IEnumerable<string> EnumerateJavaCandidates()
    {
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var managed in EnumerateManagedJavaCandidates(ext))
            yield return managed;

        foreach (var envName in new[] { "JAVA8_HOME", "JDK8_HOME", "JAVA_HOME", "JRE_HOME" })
        {
            var fromEnv = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                var p = Path.Combine(fromEnv, "bin", "java" + ext);
                if (File.Exists(p)) yield return p;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            }.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                foreach (var vendor in new[] { "Eclipse Adoptium", "Java", "Microsoft", "Zulu", "Amazon Corretto" })
                {
                    var vendorDir = Path.Combine(root, vendor);
                    if (!Directory.Exists(vendorDir)) continue;
                    IEnumerable<string> javaFiles;
                    try { javaFiles = Directory.EnumerateFiles(vendorDir, "java.exe", SearchOption.AllDirectories).ToList(); }
                    catch { continue; }
                    foreach (var javaExe in javaFiles) yield return javaExe;
                }
            }
        }

        yield return "java" + ext;
        if (OperatingSystem.IsWindows()) yield return "javaw.exe";
    }

    private static IEnumerable<string> EnumerateManagedJavaCandidates(string ext)
    {
        if (!Directory.Exists(AppSettings.JavaRoot)) yield break;
        foreach (var javaExe in Directory.EnumerateFiles(AppSettings.JavaRoot, "java" + ext, SearchOption.AllDirectories))
            yield return javaExe;
    }

    private static async Task<JavaSelection> DownloadManagedJavaAsync(
        int major,
        Guid packId,
        Action<string> report,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Automatic Java download is currently only configured for Windows.");

        var installDir = Path.Combine(AppSettings.JavaRoot, major.ToString());
        if (Directory.Exists(installDir)) Directory.Delete(installDir, recursive: true);
        Directory.CreateDirectory(installDir);

        var tempZip = Path.Combine(Path.GetTempPath(), $"cloudlauncher-java-{major}-{Guid.NewGuid():N}.zip");
        try
        {
            var url = $"https://api.adoptium.net/v3/binary/latest/{major}/ga/windows/x64/jre/hotspot/normal/eclipse?project=jdk";
            report($"Downloading Java {major} from Adoptium...");
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buffer = new byte[1024 * 128];
                long readTotal = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    readTotal += read;
                    if (total is > 0)
                    {
                        var fraction = Math.Min(0.9, readTotal / (double)total.Value * 0.9);
                        var current = Math.Min(1.0, readTotal / (double)total.Value);
                        ProgressHub.Report(packId,
                            fraction,
                            $"Downloading Java {major}... {(int)(current * 100)}%",
                            current,
                            $"Java {major} archive");
                    }
                    else
                    {
                        ProgressHub.Indeterminate(packId, $"Downloading Java {major}...", $"Java {major} archive");
                    }
                }
            }

            report($"Extracting Java {major}...");
            ProgressHub.Report(packId, 0.95, $"Extracting Java {major}...", -1, $"Extracting Java {major}");
            // Extract like any other download: every entry must stay inside the install folder and
            // within its declared size. The -version check below catches an incomplete runtime.
            using (var archive = ZipFile.OpenRead(tempZip))
            {
                var unpacked = SafeZip.ExtractToDirectory(archive, installDir, overwrite: true, ct: ct);
                if (unpacked.Skipped.Count > 0)
                    AppLog.Log("java", $"Left {unpacked.Skipped.Count} entr(ies) of the Java {major} archive out: "
                                       + string.Join(", ", unpacked.Skipped.Take(5)));
            }

            var ext = OperatingSystem.IsWindows() ? ".exe" : "";
            var javaPath = Directory.EnumerateFiles(installDir, "java" + ext, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (javaPath is null)
                throw new InvalidOperationException($"Java {major} downloaded, but java.exe was not found in the archive.");

            var detectedMajor = TryGetJavaMajor(javaPath);
            if (major == 8 ? detectedMajor != 8 : detectedMajor < major)
                throw new InvalidOperationException($"Downloaded Java at {javaPath} is not compatible.");

            report($"Java {detectedMajor} installed at {javaPath}");
            _detectedJava = null; // the pickers should list the new runtime
            return new JavaSelection(javaPath, detectedMajor, $"Downloaded Java {detectedMajor}");
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    private static int RequiredJavaMajor(string? mcVersion)
    {
        if (!TryParseMinecraftMinor(mcVersion, out var minor)) return 17;
        return minor >= 21 ? 21 : minor >= 17 ? 17 : 8;
    }

    private static bool TryParseMinecraftMinor(string? mcVersion, out int minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(mcVersion)) return false;
        var parts = mcVersion.Split('.', '-', '_');
        return parts.Length >= 2 && int.TryParse(parts[1], out minor);
    }

    private static int? TryGetJavaMajor(string javaPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);

            var match = System.Text.RegularExpressions.Regex.Match(output, @"version ""(?<major>\d+)(?:\.(?<minor>\d+))?");
            if (!match.Success) return null;
            var major = int.Parse(match.Groups["major"].Value);
            if (major == 1 && int.TryParse(match.Groups["minor"].Value, out var legacyMinor))
                return legacyMinor;
            return major;
        }
        catch { return null; }
    }

    private static string RedactLaunchArguments(string arguments) =>
        System.Text.RegularExpressions.Regex.Replace(
            arguments,
            @"(?<=--accessToken\s+)(?:""[^""]+""|\S+)",
            "<redacted>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static MinecraftPath CreateMinecraftPath(string runtimeDir, string gameDir)
    {
        var path = new MinecraftPath(gameDir)
        {
            Library  = Path.Combine(runtimeDir, "libraries"),
            Versions = Path.Combine(runtimeDir, "versions"),
            Resource = Path.Combine(runtimeDir, "resources"),
            Assets   = Path.Combine(runtimeDir, "assets"),
            Runtime  = Path.Combine(runtimeDir, "runtime")
        };
        path.CreateDirs();
        return path;
    }

    /// <summary>Wires stdout/stderr batching for the process and returns the flush timer, so the
    /// caller can dispose it if the process never starts. Otherwise only the Exited handler stops
    /// it, and it would leak along with everything it references.</summary>
    private static System.Timers.Timer ConfigureProcessLogging(Process process, string gameDir, Action<string> report)
    {
        var psi = process.StartInfo;
        psi.WorkingDirectory = gameDir;
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        report($"Java: {psi.FileName}");
        report($"Working directory: {psi.WorkingDirectory}");
        report($"Arguments: {RedactLaunchArguments(psi.Arguments)}");

        process.EnableRaisingEvents = true;

        // Modded Minecraft prints hundreds of lines per second during startup. Forwarding each one
        // floods the dispatcher and starves input (hotkeys, mouse), so buffer lines and flush them
        // in one report() call every 150 ms.
        var lineBuffer = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var flushTimer = new System.Timers.Timer(150) { AutoReset = true };
        flushTimer.Elapsed += (_, _) => FlushLogBuffer(lineBuffer, report);
        flushTimer.Start();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) lineBuffer.Enqueue("[minecraft] " + e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) lineBuffer.Enqueue("[minecraft] " + e.Data);
        };
        process.Exited += (_, _) =>
        {
            flushTimer.Stop();
            flushTimer.Dispose();
            FlushLogBuffer(lineBuffer, report);
            try { report($"Minecraft exited with code {process.ExitCode}."); }
            catch { report("Minecraft exited."); }
        };
        return flushTimer;
    }

    private static void FlushLogBuffer(
        System.Collections.Concurrent.ConcurrentQueue<string> buffer,
        Action<string> report)
    {
        if (buffer.IsEmpty) return;
        var sb = new StringBuilder();
        while (buffer.TryDequeue(out var line))
            sb.AppendLine(line);
        report(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// Checks that a compatible Java is available. Older Minecraft/Forge needs Java 8 specifically.
    /// </summary>
    public static (bool ok, string message) CheckJava(string? mcVersion, string? overridePath = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            {
                var major = TryGetJavaMajor(overridePath);
                if (major is not null) return (true, $"Java {major} ({overridePath})");
            }
            return TryResolveJavaExecutable(mcVersion, out var java, out var diagnostic)
                ? (true, java.Message)
                : (true, $"Java will be downloaded automatically ({diagnostic}).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static Task<(bool ok, string message)> CheckJavaAsync(string? mcVersion, string? overridePath = null, CancellationToken ct = default)
    {
        return Task.Run(() => CheckJava(mcVersion, overridePath), ct);
    }
}
