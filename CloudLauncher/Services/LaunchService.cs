using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<Process> LaunchTrackedAsync(
        PackDetail pack,
        IProgress<string>? log,
        CancellationToken ct = default)
    {
        using var launch = instances.BeginLaunch(pack.Id, ct);
        var process = await LaunchAsync(pack, log, launch.Token);
        var launchToken = launch.Token;
        if (!launch.Complete(process))
            throw new OperationCanceledException("Launch was cancelled.", launchToken);

        return process;
    }

    public async Task<Process> LaunchAsync(
        PackDetail pack,
        IProgress<string>? log,
        CancellationToken ct = default)
    {
        return await Task.Run(() => LaunchCoreAsync(pack, log, ct), ct);
    }

    private async Task<Process> LaunchCoreAsync(
        PackDetail pack,
        IProgress<string>? log,
        CancellationToken ct = default)
    {
        void Report(string m) { log?.Report(m); AppLog.Log("launch", m); }

        if (pack.IsEmpty)
            throw new InvalidOperationException("This pack is marked as empty — it can't launch Minecraft.");
        if (string.IsNullOrEmpty(pack.MinecraftVersion))
            throw new InvalidOperationException("Pack has no Minecraft version set.");

        Report($"Launching pack '{pack.Name}' ({pack.MinecraftVersion}, {pack.Loader})");
        ProgressHub.Indeterminate(pack.Id, "Preparing…");

        // Libraries, assets and version JARs go into the SHARED runtime directory,
        // so they are downloaded once and reused across all packs.
        var runtimeDir = AppSettings.RuntimeRoot;
        Directory.CreateDirectory(runtimeDir);
        var gameDir = packs.GameDir(pack.Id);
        Directory.CreateDirectory(gameDir);
        // Overlay local/ files into game/ (local/ holds per-user additions not synced to the server).
        // Shared files are already in game/ — synced there directly, so no shared/ overlay needed.
        if (Directory.Exists(packs.LocalDir(pack.Id)))
            Report("Overlaying local/ into game/ for launch…");
        packs.PrepareLaunchOverlay(pack.Id);
        // Forcing windowed + no-pause-on-lost-focus only exists so the game can be
        // embedded in the custom host window. When that's disabled, leave the pack's
        // options.txt alone so fullscreen and pause behave as Minecraft normally would.
        if (settings.UseCustomGameWindow)
        {
            OptionsTxtService.EnsureWindowed(gameDir);
            OptionsTxtService.EnsureNoPauseOnLostFocus(gameDir);
        }
        var path = CreateMinecraftPath(runtimeDir, gameDir);
        Report($"Runtime: {runtimeDir}");
        Report($"Game dir: {gameDir}");

        var launcher = new MinecraftLauncher(path);
        var overallFraction = -1.0;
        var overallLabel = "Installing files…";
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
            var label = overallFraction >= 0 ? overallLabel : $"Downloading… {(int)(frac * 100)}%";
            ProgressHub.Report(pack.Id,
                overallFraction,
                label,
                frac,
                string.IsNullOrWhiteSpace(currentResourceLabel) ? "Current resource" : currentResourceLabel);
        };

        Report("Resolving Minecraft version…");
        var versionId = await ResolveVersionAsync(launcher, pack, log, ct);
        Report($"Version: {versionId}");

        Report("Resolving Minecraft account…");
        ProgressHub.Indeterminate(pack.Id, "Signing in…");
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

        ProgressHub.Indeterminate(pack.Id, "Installing files…");
        Report($"Installing/checking files for {versionId}…");

        JavaSelection java;
        try
        {
            // Bootstrap the game with the Java the Minecraft version itself needs
            // (e.g. Forge 1.12.2's launchwrapper only works on Java 8). If the pack
            // ships the ReLauncher mod it relaunches into its own target JVM afterwards.
            java = await EnsureJavaExecutableAsync(pack.MinecraftVersion, pack.Id, Report, ct);
        }
        catch
        {
            ProgressHub.Clear(pack.Id);
            throw;
        }
        Report($"Selected Java: {java.Path} ({java.Message})");

        // Keep config/relauncher.json's javaPath valid and pointed at the JVM the pack's
        // ReLauncher mod should relaunch into (its targetJavaVersion, e.g. J25) — NOT the
        // Java we bootstrap with. Fixes empty/stale/wrong paths.
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

        Report("Files ready. Starting Minecraft…");
        ProgressHub.Report(pack.Id, 1.0, "Starting Minecraft…");
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
            // The game never started, so the Exited handler that normally disposes the flush
            // timer will not fire — dispose it here to avoid leaking the timer + log-sink chain.
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

    public async Task<Process> StartLocalServerAsync(
        PackDetail pack,
        IProgress<string>? log,
        CancellationToken ct = default)
    {
        if (pack.IsEmpty)
            throw new InvalidOperationException("Empty packs can't run as a server.");
        if (string.IsNullOrEmpty(pack.MinecraftVersion))
            throw new InvalidOperationException("Pack has no Minecraft version set.");

        var packRoot = packs.PackRoot(pack.Id);
        var gameDir = packs.GameDir(pack.Id);
        var serverOverrideDir = Path.Combine(packRoot, "server");
        Directory.CreateDirectory(serverOverrideDir);
        var serverRunDir = Path.Combine(packRoot, "server-run");
        Directory.CreateDirectory(serverRunDir);

        if (Directory.Exists(packs.LocalDir(pack.Id)))
        {
            log?.Report("Overlaying local/ into game/ for server launch…");
            packs.PrepareLaunchOverlay(pack.Id);
        }

        log?.Report("Building server directory: game → server-run, then overlaying server/...");
        CopyDirectory(gameDir, serverRunDir, overwrite: true);
        CopyDirectory(serverOverrideDir, serverRunDir, overwrite: true);

        // Accept the EULA. Per Mojang's terms, this must reflect the user's actual acceptance —
        // by clicking "Start server" the launcher's user is agreeing to it.
        File.WriteAllText(Path.Combine(serverRunDir, "eula.txt"), "eula=true\n");

        var serverJar = Path.Combine(serverRunDir, "server.jar");
        if (!File.Exists(serverJar))
        {
            log?.Report($"Downloading vanilla server jar for {pack.MinecraftVersion}...");
            await DownloadVanillaServerJarAsync(pack.MinecraftVersion!, serverJar, log, ct);
        }

        var java = await EnsureJavaExecutableAsync(pack.MinecraftVersion, pack.Id, m =>
        {
            log?.Report(m);
            AppLog.Log("server", m);
        }, ct);
        log?.Report($"Java: {java.Path} ({java.Message})");

        await SyncRelauncherJavaAsync(serverRunDir, java, pack.Id, m => log?.Report(m), ct);

        log?.Report("Launching server (Ctrl-C in console to stop)...");

        var psi = new ProcessStartInfo
        {
            FileName = java.Path,
            WorkingDirectory = serverRunDir,
            UseShellExecute = true, // open a new console so the user can see/interact
            CreateNoWindow = false,
            Arguments = $"-Xmx{settings.GetMaxRamFor(pack.Id)}M -Xms1G -jar \"{serverJar}\" nogui"
        };
        var p = new Process { StartInfo = psi };
        p.Start();
        return p;
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

    private static Task<JavaSelection> EnsureJavaExecutableAsync(
        string? mcVersion,
        Guid packId,
        Action<string> report,
        CancellationToken ct)
        => EnsureJavaMajorAsync(RequiredJavaMajor(mcVersion), packId, report, ct);

    /// <summary>
    /// Keeps a pack's <c>config/relauncher.json</c> javaPath valid. The game is
    /// bootstrapped with <paramref name="launchJava"/> (the Minecraft-compatible JVM);
    /// when the ReLauncher mod is enabled and targets a different Java major, we
    /// provision that JVM and point javaPath at it so the mod's relaunch succeeds.
    /// Otherwise we just repair the path to the bootstrap Java.
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
                // Don't bail out: a stale/empty javaPath makes the ReLauncher mod fail with
                // "missing the java machine" at startup. Fall back to the bootstrap Java so the
                // path is at least valid — better a working JVM than a broken relaunch.
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

        // Match the required major EXACTLY. A newer JDK is not a safe substitute:
        //   • Forge 1.20.1 ships ASM that can't read Java 25 classes
        //     ("Unsupported class file major version 69").
        //   • Forge 1.12.2 needs Java 8's URLClassLoader.
        // So we never reuse a higher Java just because one happens to be installed
        // (e.g. a Java 25 we provisioned for a pack's ReLauncher mod) — we resolve, and
        // if necessary download, the exact major the version needs.
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
            ZipFile.ExtractToDirectory(tempZip, installDir, overwriteFiles: true);

            var ext = OperatingSystem.IsWindows() ? ".exe" : "";
            var javaPath = Directory.EnumerateFiles(installDir, "java" + ext, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (javaPath is null)
                throw new InvalidOperationException($"Java {major} downloaded, but java.exe was not found in the archive.");

            var detectedMajor = TryGetJavaMajor(javaPath);
            if (major == 8 ? detectedMajor != 8 : detectedMajor < major)
                throw new InvalidOperationException($"Downloaded Java at {javaPath} is not compatible.");

            report($"Java {detectedMajor} installed at {javaPath}");
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

    /// <summary>Wires stdout/stderr batching for the process and returns the flush timer so the
    /// caller can dispose it if the process never starts (otherwise the 150 ms timer — and the
    /// buffer/report/view chain it pins — leaks forever, since only the Exited handler stops it).</summary>
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

        // Modded Minecraft emits hundreds of stdout/stderr lines per second during
        // startup. Forwarding each line individually produces ~1000 Normal-priority
        // dispatcher operations per second, which starves WM_HOTKEY and mouse-input
        // messages (processed at Input priority) — making keybindings and window
        // interactions unresponsive while the modpack loads.
        // Instead, buffer lines and flush them as a single report() call every 150 ms
        // so the dispatcher queue stays clear for input.
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
    /// Checks that a compatible Java is available. Older Minecraft/Forge needs Java 8 exactly.
    /// </summary>
    public static (bool ok, string message) CheckJava(string? mcVersion)
    {
        try
        {
            return TryResolveJavaExecutable(mcVersion, out var java, out var diagnostic)
                ? (true, java.Message)
                : (true, $"Java will be downloaded automatically ({diagnostic}).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static Task<(bool ok, string message)> CheckJavaAsync(string? mcVersion, CancellationToken ct = default)
    {
        return Task.Run(() => CheckJava(mcVersion), ct);
    }
}
