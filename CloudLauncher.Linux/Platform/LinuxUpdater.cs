using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CloudLauncher.Services;
using CloudLauncher.Shared;
using CloudLauncher.Views;

namespace CloudLauncher;

/// <summary>An update that cannot be installed for a reason the user can act on (the launcher's folder
/// is read-only, the build is for another kind of computer...). The message is a sentence for the
/// screen; nothing was changed.</summary>
public sealed class UpdateBlockedException(string message) : Exception(message);

/// <summary>
/// Self-update for the Linux launcher. The server's feed (<c>launcher/linux/latest</c>) points at a
/// <c>.tar.gz</c> signed exactly like the Windows packages: nothing is installed unless the release's
/// manifest verifies against the compiled-in keys (<see cref="UpdateVerifier"/>) and the download
/// matches that manifest byte for byte.
/// </summary>
/// <remarks>
/// <para>Applying unpacks the package into a work folder beside the running program (same file system,
/// so every file goes into place by rename: a running Linux program may be replaced that way, it keeps
/// its old file until it exits), checks that the new program starts (<c>--version</c>), moves the files
/// over the installed ones with the program last, starts the new launcher once this one has exited, and
/// shuts this one down.</para>
/// <para>The tarball holds one top folder, <c>cloudlauncher/</c>, with the single-file program
/// <c>cloudlauncher</c>, install.sh, the icon and the licence files (tools/release/pack-linux.py).</para>
/// </remarks>
public static class LinuxUpdater
{
    /// <summary>Where the launcher can always be downloaded by hand.</summary>
    public const string WebsiteUrl = UpdateService.WebsiteUrl;

    /// <summary>The work folder's name, beside the running program.</summary>
    private const string WorkFolderName = ".cloudlauncher-update";

    /// <summary>The program's name inside the package.</summary>
    private const string ProgramName = "cloudlauncher";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Http = CreateHttp();

    /// <summary>Held from the first download until this process exits once the files are swapped, so a
    /// second click cannot start another update on the same folder.</summary>
    private static readonly SemaphoreSlim Applying = new(1, 1);

    /// <summary>The newer, verified release the startup check found, if any.</summary>
    public static LauncherReleaseInfo? Available { get; private set; }

    private static HttpClient CreateHttp()
    {
        // No overall timeout: a download can take minutes. The feed calls set their own, and the
        // download gives up when the server goes quiet (see DownloadVerifiedAsync).
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.None
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CloudLauncher/{AppVersion.CurrentString}");
        return http;
    }

    private static string ServerUrl => App.State.Settings.ServerUrl.Trim().TrimEnd('/');

    // ── the feed ──────────────────────────────────────────────────────────────

    /// <summary>The latest Linux release on the server, or null when none is published (204, or 404
    /// from a server without the Linux routes).</summary>
    /// <exception cref="HttpRequestException">The server could not be reached or refused.</exception>
    /// <exception cref="TimeoutException">The server did not answer within 30 s.</exception>
    public static async Task<LauncherReleaseInfo?> GetLatestAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var resp = await Http.GetAsync(ServerUrl + "/launcher/linux/latest", timeout.Token);
            if (resp.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return null;
            EnsureAnswered(resp);
            return await resp.Content.ReadFromJsonAsync<LauncherReleaseInfo>(Json, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The server did not answer in time.");
        }
        catch (JsonException)
        {
            throw new HttpRequestException("The server's answer was not a release.");
        }
    }

    /// <summary>The latest release when it is newer than this launcher, else null. Not checked for a
    /// signature: <see cref="Verify"/> decides whether it may be installed.</summary>
    public static async Task<LauncherReleaseInfo?> CheckAsync(CancellationToken ct = default)
    {
        var latest = await GetLatestAsync(ct);
        return latest is not null && AppVersion.IsNewer(latest.Version) ? latest : null;
    }

    /// <summary>Whether <paramref name="release"/> may be installed: the server is reached over https
    /// (or http on this machine) and the release is signed for a version newer than this one.</summary>
    public static UpdateVerification Verify(LauncherReleaseInfo release) =>
        AppSettings.IsSecureServerUrl(App.State.Settings.ServerUrl)
            ? UpdateVerifier.Verify(release)
            : new UpdateVerification(null, $"updates are only installed over https, and the server address is {ServerUrl}");

    /// <summary>
    /// The release notes for What's New, newest first: the Linux launcher's own releases, or the Windows
    /// launcher's (the two share their version numbers) when the server has no Linux list yet.
    /// <c>Linux</c> says which.
    /// </summary>
    public static async Task<(IReadOnlyList<LauncherReleaseInfo> Releases, bool Linux)> GetReleasesAsync(CancellationToken ct = default)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                using var resp = await Http.GetAsync(ServerUrl + "/launcher/linux/releases", timeout.Token);
                if (resp.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotFound))
                {
                    EnsureAnswered(resp);
                    var list = await resp.Content.ReadFromJsonAsync<List<LauncherReleaseInfo>>(Json, timeout.Token);
                    if (list is { Count: > 0 }) return (list, true);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The server did not answer in time.");
            }
            catch (JsonException)
            {
                throw new HttpRequestException("The server's answer was not a list of releases.");
            }
        }
        return (await App.State.Api.GetLauncherReleasesAsync(ct), false);
    }

    /// <summary>A failed check or download in words.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        ApiException api => ApiClient.ServerSentence(api) ?? api.Message,
        HttpRequestException { InnerException: { } inner } when inner.Message.Length > 0 => "Could not reach the server: " + inner.Message.TrimEnd('.') + ".",
        HttpRequestException http => http.Message,
        _ => ex.Message
    };

    private static void EnsureAnswered(HttpResponseMessage resp)
    {
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"The server answered {(int)resp.StatusCode} {resp.ReasonPhrase}.", null, resp.StatusCode);
    }

    // ── the startup check ────────────────────────────────────────────────────

    /// <summary>Looks for a newer release once, in the background, a little after start, and says so on
    /// the status line. Never installs anything by itself.</summary>
    public static void CheckInBackground()
    {
        if (!OperatingSystem.IsLinux()) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(6));
                if (await CheckAsync() is not { } release) return;
                var check = Verify(release);
                if (!check.IsValid)
                {
                    AppLog.Log("update", $"CloudLauncher {release.Version} is not offered: {check.Failure}.");
                    return;
                }
                Available = release;
                AppLog.Log("update", $"CloudLauncher {release.Version} is available.");
                Dispatcher.UIThread.Post(() =>
                    MainWindow.Current?.Toast($"CloudLauncher {release.Version} is out: Settings > Check for Updates"));
            }
            catch (Exception ex)
            {
                AppLog.Log("update", "Could not look for a new version: " + Describe(ex));
            }
        });
    }

    // ── applying ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads <paramref name="release"/>, proves it, puts it in place of the running launcher, starts
    /// the new one and shuts this one down. Cancelling works until the files are swapped.
    /// </summary>
    /// <param name="status">Optional: what it is doing, in words ("Checking the new version...").</param>
    /// <exception cref="UpdateVerificationException">The release or the download did not match its
    /// signature; nothing was installed.</exception>
    /// <exception cref="UpdateBlockedException">It cannot be installed here (a read-only folder, a build
    /// for another processor, a development copy, or the new version does not start); nothing was
    /// changed. The message says what to do.</exception>
    public static async Task DownloadAndApplyAsync(LauncherReleaseInfo release, IProgress<double>? progress = null,
        CancellationToken ct = default, IProgress<string>? status = null)
    {
        if (!OperatingSystem.IsLinux())
            throw new UpdateBlockedException("This updater installs the Linux launcher, and this computer is not running Linux.");

        var check = Verify(release);
        if (!check.IsValid || check.Manifest is not { } manifest)
            throw Refused(check.Failure ?? "the release could not be verified");
        if (OtherArchitecture(release.FileName) is { } other)
            throw new UpdateBlockedException($"CloudLauncher {release.Version} on the server is built for {other} processors, "
                                             + $"and this computer needs the {OwnArchitecture} build. Download it from {WebsiteUrl}.");

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            throw new UpdateBlockedException($"The launcher cannot tell where it runs from, so it cannot update itself. Download CloudLauncher {release.Version} from {WebsiteUrl}.");
        var installDir = Path.GetDirectoryName(exe)!;
        if (File.Exists(Path.Combine(installDir, ProgramName + ".dll")))
            throw new UpdateBlockedException("This is a development build of the launcher, which does not update itself.");

        if (!await Applying.WaitAsync(0, ct))
            throw new InvalidOperationException("An update is already being downloaded.");
        var work = Path.Combine(installDir, WorkFolderName);
        var installed = false;
        try
        {
            try
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
                Directory.CreateDirectory(work);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                AppLog.Log("update", $"The install folder {installDir} cannot be written: {ex.Message}");
                throw new UpdateBlockedException($"The launcher cannot write to its folder ({installDir}), so it cannot update itself. "
                                                 + $"Download CloudLauncher {release.Version} from {WebsiteUrl} instead.");
            }

            var package = Path.Combine(work, "package.tar.gz");
            status?.Report("Downloading...");
            await DownloadVerifiedAsync(manifest, package, progress, ct);

            status?.Report("Unpacking...");
            var staged = await Task.Run(() => Extract(package, Path.Combine(work, "staging"), ct), ct);

            status?.Report("Checking the new version...");
            await CheckStartsAsync(Path.Combine(staged, ProgramName), manifest.Version, ct);

            ct.ThrowIfCancellationRequested();
            // From here on there is no cancelling: the swap is not given the token.
            status?.Report("Installing...");
            await Task.Run(() => Swap(staged, installDir, exe));
            installed = true;
            AppLog.Log("update", $"Installed CloudLauncher {manifest.Version} in {installDir}; restarting.");
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (Exception ex) { AppLog.LogError("update", ex); }
            // Installed, the lock stays taken until this process exits, which is next.
            if (!installed) Applying.Release();
        }

        status?.Report("Restarting...");
        Restart(exe);
    }

    /// <summary>Starts this launcher's program again once this process has exited, then shuts this one
    /// down. For an update, and for settings that only apply at start (the server address).</summary>
    /// <remarks>The new process waits for this one to be gone, because a second launcher on the same
    /// profile only asks the first one to come forward (LinuxSingleInstance). It gives up waiting after a
    /// minute and starts anyway.</remarks>
    public static void Restart(string? exe = null)
    {
        exe ??= Environment.ProcessPath;
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(exe))
            throw new InvalidOperationException("Restart the launcher yourself to finish.");

        const string waitThenStart =
            "i=0; " +
            "while [ \"$i\" -lt 300 ] && [ -d \"/proc/$1\" ]; do " +
            "  case $(sed -n 's/^State:[[:space:]]*\\([A-Z]\\).*/\\1/p' \"/proc/$1/status\" 2>/dev/null) in Z|X|'') break ;; esac; " +
            "  sleep 0.2 2>/dev/null || sleep 1; i=$((i+1)); " +
            "done; " +
            "exec \"$0\"";
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(waitThenStart);
        psi.ArgumentList.Add(exe);
        psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Process.Start(psi)?.Dispose();
        AppLog.Log("app", "Restarting.");

        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
            else Environment.Exit(0);
        });
    }

    /// <summary>Streams the package the manifest names into <paramref name="path"/>, hashing it on the
    /// way, and throws unless it is the signed size and SHA-256.</summary>
    private static async Task DownloadVerifiedAsync(UpdateManifest manifest, string path, IProgress<double>? progress,
        CancellationToken ct)
    {
        var sha = manifest.Sha256.ToLowerInvariant();
        var resp = await Http.GetAsync($"{ServerUrl}/launcher/linux/download?sha256={sha}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // No copy under that hash: the current package, which is checked against the manifest all the same.
            resp.Dispose();
            resp = await Http.GetAsync($"{ServerUrl}/launcher/linux/download", HttpCompletionOption.ResponseHeadersRead, ct);
        }
        using (resp)
        {
            EnsureAnswered(resp);
            if (resp.Content.Headers.ContentLength is { } declared && declared != manifest.Size)
                throw Refused($"the server offered {declared} bytes, but the signed package is {manifest.Size}");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // Gives up when nothing arrives for a minute, rather than waiting forever on a dead connection.
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
            long read = 0;
            try
            {
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(path);
                var buffer = new byte[81920];
                while (true)
                {
                    quiet.CancelAfter(TimeSpan.FromSeconds(60));
                    var n = await src.ReadAsync(buffer, quiet.Token);
                    if (n == 0) break;
                    read += n;
                    // Stop at the first byte past the signed size instead of filling the disk.
                    if (read > manifest.Size)
                        throw Refused($"the download is larger than the {manifest.Size} bytes that were signed");
                    hash.AppendData(buffer, 0, n);
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    progress?.Report((double)read / manifest.Size);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The download stopped: nothing came from the server for a minute.");
            }

            var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!UpdateVerifier.MatchesPackage(manifest, sha256, read))
                throw Refused($"the download ({read} bytes, sha256 {sha256}) is not the signed package "
                              + $"({manifest.Size} bytes, sha256 {manifest.Sha256})");
        }
        AppLog.Log("update", $"Downloaded CloudLauncher {manifest.Version} for Linux; it matches its signed manifest.");
    }

    /// <summary>Unpacks the package into <paramref name="staging"/> and returns its top folder. Only
    /// folders and plain files are taken, all of them inside one top folder; anything else (a link, a
    /// device, a path leaving the folder) refuses the whole package.</summary>
    private static string Extract(string package, string staging, CancellationToken ct)
    {
        Directory.CreateDirectory(staging);
        var root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string? top = null;

        using (var file = File.OpenRead(package))
        using (var gz = new GZipStream(file, CompressionMode.Decompress))
        using (var reader = new TarReader(gz))
        {
            while (reader.GetNextEntry() is { } entry)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue;

                var name = entry.Name.Replace('\\', '/');
                while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
                name = name.TrimEnd('/');
                if (name.Length == 0) continue;
                var parts = name.Split('/');
                if (name.StartsWith('/') || parts.Any(p => p is "" or "." or ".."))
                    throw Refused($"the package has an entry outside its folder ({entry.Name})");
                top ??= parts[0];
                if (!string.Equals(parts[0], top, StringComparison.Ordinal))
                    throw Refused("the package has more than one top folder");

                var target = Path.GetFullPath(Path.Combine(staging, name));
                if (!target.StartsWith(root, StringComparison.Ordinal))
                    throw Refused($"the package has an entry outside its folder ({entry.Name})");

                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(target);
                        break;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, overwrite: false);
                        break;
                    default:
                        throw Refused($"the package holds a {entry.EntryType} entry ({entry.Name}), which a release never has");
                }
            }
        }

        if (top is null) throw Refused("the package is empty");
        var folder = Path.Combine(staging, top);
        var program = Path.Combine(folder, ProgramName);
        if (!Directory.Exists(folder) || !File.Exists(program))
            throw Refused($"the package has no {ProgramName} program in its {top}/ folder");

        const UnixFileMode executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(program, executable);
        var script = Path.Combine(folder, "install.sh");
        if (File.Exists(script)) File.SetUnixFileMode(script, executable);
        return folder;
    }

    /// <summary>Runs the new program with <c>--version</c> (Program.Main answers that before anything
    /// else starts) and throws unless it answers with the version that was signed, so a build that cannot
    /// run here (another processor, a C library too old) never replaces one that can.</summary>
    private static async Task CheckStartsAsync(string program, string version, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(program)!
        };
        psi.ArgumentList.Add("--version");

        string output;
        int exitCode;
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("the program did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* it ended meanwhile */ }
                if (ct.IsCancellationRequested) throw;
                throw new InvalidOperationException("it did not answer within a minute");
            }
            output = await stdout;
            var errors = await stderr;
            exitCode = process.ExitCode;
            if (errors.Length > 0) AppLog.Log("update", "The new version said: " + errors.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Log("update", $"The new version did not start: {ex.Message}");
            throw NotStarting(version);
        }

        var answered = Regex.Match(output, @"CloudLauncher (\d+(?:\.\d+){1,3})");
        if (exitCode != 0 || !answered.Success
            || !Version.TryParse(answered.Groups[1].Value, out var running)
            || !Version.TryParse(version, out var signed)
            || AppVersion.Rank(running) != AppVersion.Rank(signed))
        {
            AppLog.Log("update", $"The new version answered {exitCode}: {output.Trim()}");
            throw NotStarting(version);
        }
    }

    private static UpdateBlockedException NotStarting(string version) =>
        new($"CloudLauncher {version} does not start on this computer, so it was not installed. "
            + $"Nothing has changed; the website ({WebsiteUrl}) has the requirements.");

    /// <summary>Moves the unpacked files over the installed ones by rename, the program last (onto the
    /// running program's own name), so an interruption leaves the old program in place.</summary>
    /// <remarks>The other files (install.sh, the icon, the licences, the readme) only go into a folder
    /// that is plainly a release folder, one that already has install.sh and the icon. A program kept on
    /// its own somewhere like Downloads gets only the new program, so nobody's own README.txt beside it
    /// is overwritten.</remarks>
    private static void Swap(string staged, string installDir, string exe)
    {
        var program = Path.Combine(staged, ProgramName);
        var releaseFolder = File.Exists(Path.Combine(installDir, "install.sh"))
                            && File.Exists(Path.Combine(installDir, "cloudlauncher.png"));
        if (releaseFolder)
        {
            foreach (var file in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(file, program, StringComparison.Ordinal)) continue;
                var target = Path.Combine(installDir, Path.GetRelativePath(staged, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target, overwrite: true);
            }
        }
        else
        {
            AppLog.Log("update", $"{installDir} is not a release folder; only the program is replaced.");
        }
        File.Move(program, exe, overwrite: true);
    }

    /// <summary>The processor a release's file name says it is for, when that is not this one's
    /// (<c>CloudLauncher-0.9.19-linux-x64.tar.gz</c> is for x64). Null when it is ours or the name says
    /// nothing; the name is not signed, so it is only ever used to refuse.</summary>
    private static string? OtherArchitecture(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        var match = Regex.Match(fileName, @"-linux-([a-z0-9]+)\.tar\.gz$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var arch = match.Groups[1].Value.ToLowerInvariant();
        return arch == OwnArchitecture ? null : arch;
    }

    private static string OwnArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        Architecture.X86 => "x86",
        var other => other.ToString().ToLowerInvariant()
    };

    private static UpdateVerificationException Refused(string reason)
    {
        AppLog.Log("update", $"Update refused: {reason}.");
        return new UpdateVerificationException(reason);
    }
}
