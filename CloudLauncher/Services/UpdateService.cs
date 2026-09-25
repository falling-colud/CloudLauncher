using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>Thrown when an update could not be proven to be the signed release. Nothing was
/// installed.</summary>
public sealed class UpdateVerificationException(string reason)
    : Exception("The update could not be verified, so it was not installed. Download it from the website instead.")
{
    /// <summary>What did not match, for the log.</summary>
    public string Reason { get; } = reason;
}

/// <summary>Checks the server for a newer launcher build and applies it.</summary>
/// <remarks>
/// <para>A running exe can't overwrite itself, so the package (a zip of the publish output) is
/// extracted to a staging folder, and a small PowerShell script waits for this process to exit,
/// copies the files over the install directory and relaunches.</para>
/// <para>Nothing is offered or installed unless the server address is https, the release has a
/// valid signature (see <see cref="UpdateVerifier"/>), and the download matches its signed manifest
/// byte for byte.</para>
/// </remarks>
public sealed class UpdateService
{
    /// <summary>Where the launcher can always be downloaded by hand.</summary>
    public const string WebsiteUrl = "https://cloudlauncher.co";

    private readonly ApiClient _api;
    private readonly string _work;
    private readonly string _failureReport;

    /// <param name="workRoot">Scratch folder for the download; a test can point it elsewhere.</param>
    /// <param name="failureReportPath">Where the updater leaves its log when an update fails. Outside
    /// the work folder, which the next attempt deletes.</param>
    public UpdateService(ApiClient api, string? workRoot = null, string? failureReportPath = null)
    {
        _api = api;
        _work = workRoot ?? Path.Combine(Path.GetTempPath(), "CloudLauncherUpdate");
        _failureReport = failureReportPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudLauncher", "update-failed.log");
        LastUpdateFailed = ReportFailedUpdate();
    }

    /// <summary>True when the previous update did not install. Its log has been copied into the app
    /// log.</summary>
    public bool LastUpdateFailed { get; }

    public Task<LauncherReleaseInfo?> GetLatestAsync(CancellationToken ct = default) =>
        _api.GetLatestLauncherAsync(ct);

    /// <summary>Returns the latest release if it is newer than the running build and verifies,
    /// otherwise null. Swallows network errors so callers can use it as a best-effort check on
    /// launch.</summary>
    public async Task<LauncherReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var latest = await _api.GetLatestLauncherAsync(ct);
            if (latest is null || !AppVersion.IsNewer(latest.Version)) return null;
            var check = Verify(latest);
            if (check.IsValid) return latest;
            AppLog.Log("update", $"Launcher {latest.Version} is not offered: {check.Failure}.");
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Whether <paramref name="release"/> may be installed: the server is reached over https
    /// (or http on this machine), and the release is signed for a version newer than this one.</summary>
    public UpdateVerification Verify(LauncherReleaseInfo release) =>
        AppSettings.IsSecureServerUrl(_api.ServerUrl)
            ? UpdateVerifier.Verify(release)
            : new UpdateVerification(null, $"updates are only installed over https, and the server address is {_api.ServerUrl}");

    /// <summary>Installs the latest release, with the same checks as the overload that takes one.</summary>
    public async Task DownloadAndApplyAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var latest = await _api.GetLatestLauncherAsync(ct)
                     ?? throw new InvalidOperationException("No launcher release is published on the server.");
        await DownloadAndApplyAsync(latest, progress, ct);
    }

    /// <summary>Downloads and stages <paramref name="release"/>, launches the external updater, and
    /// signals the caller that it should shut the application down so the files can be replaced.</summary>
    /// <exception cref="UpdateVerificationException">The release or the download did not match its
    /// signature. The work folder is gone and nothing was installed.</exception>
    public async Task DownloadAndApplyAsync(
        LauncherReleaseInfo release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var check = Verify(release);
        if (!check.IsValid || check.Manifest is not { } manifest)
            throw Refused(check.Failure ?? "the release could not be verified");

        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var exePath = Process.GetCurrentProcess().MainModule?.FileName
                      ?? Path.Combine(installDir, "CloudLauncher.exe");

        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
        Directory.CreateDirectory(_work);

        string sourceRoot;
        try
        {
            var zipPath = Path.Combine(_work, "package.zip");
            await DownloadVerifiedAsync(manifest, zipPath, progress, ct);
            sourceRoot = Extract(zipPath, Path.GetFileName(exePath));
        }
        catch
        {
            // Never leave a package behind that has not been proven, nor half of one that has.
            try { Directory.Delete(_work, recursive: true); }
            catch (Exception ex) { AppLog.LogError("update", ex); }
            throw;
        }

        var scriptPath = Path.Combine(_work, "apply-update.ps1");
        await File.WriteAllTextAsync(scriptPath, UpdaterScript, ct);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" " +
                        $"-LauncherPid {Environment.ProcessId} -Source \"{sourceRoot}\" -Dest \"{installDir}\" " +
                        $"-Exe \"{exePath}\" -Report \"{_failureReport}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        };
        Process.Start(psi);
    }

    /// <summary>Streams the package named by <paramref name="manifest"/> to
    /// <paramref name="zipPath"/>, hashing it on the way, and throws unless it matches the signed size
    /// and SHA-256.</summary>
    private async Task DownloadVerifiedAsync(
        UpdateManifest manifest, string zipPath, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await _api.DownloadLauncherAsync(manifest.Sha256, ct);
        if (resp.Content.Headers.ContentLength is { } declared && declared != manifest.Size)
            throw Refused($"the server offered {declared} bytes, but the signed package is {manifest.Size}");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long read = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(zipPath))
        {
            var buffer = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                read += n;
                // Stop at the first byte past the signed size instead of filling the disk.
                if (read > manifest.Size)
                    throw Refused($"the download is larger than the {manifest.Size} bytes that were signed");
                hash.AppendData(buffer, 0, n);
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                progress?.Report((double)read / manifest.Size);
            }
        }

        var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!UpdateVerifier.MatchesPackage(manifest, sha256, read))
            throw Refused($"the download ({read} bytes, sha256 {sha256}) is not the signed package " +
                          $"({manifest.Size} bytes, sha256 {manifest.Sha256})");
        AppLog.Log("update", $"Downloaded launcher {manifest.Version}; it matches its signed manifest.");
    }

    /// <summary>Unpacks the verified package and returns the folder whose contents replace the
    /// install.</summary>
    private string Extract(string zipPath, string exeName)
    {
        var staging = Path.Combine(_work, "staging");
        ZipFile.ExtractToDirectory(zipPath, staging);

        // If the archive wraps everything in a single top-level folder, copy from inside it.
        var sourceRoot = staging;
        var topDirs = Directory.GetDirectories(staging);
        var topFiles = Directory.GetFiles(staging);
        if (topFiles.Length == 0 && topDirs.Length == 1)
            sourceRoot = topDirs[0];

        // A package without the launcher in it would leave nothing to restart.
        if (!File.Exists(Path.Combine(sourceRoot, exeName)))
            throw Refused($"the package has no {exeName}");
        return sourceRoot;
    }

    private static UpdateVerificationException Refused(string reason)
    {
        AppLog.Log("update", $"Update refused: {reason}.");
        return new UpdateVerificationException(reason);
    }

    /// <summary>Copies the updater's log into the app log when the last update did not install.
    /// Once: the report is deleted after it has been read.</summary>
    private bool ReportFailedUpdate()
    {
        try
        {
            if (!File.Exists(_failureReport)) return false;
            var log = File.ReadAllText(_failureReport).TrimEnd();
            File.Delete(_failureReport);
            AppLog.Log("update", "The last update could not be installed. The updater's log:\n" + log);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.LogError("update", ex);
            return false;
        }
    }

    // Waits for the launcher to exit, copies the new build over the install directory, then
    // relaunches. Uses only what ships with Windows.
    //
    // robocopy rather than Copy-Item: Copy-Item -Recurse copies a source subfolder *into* an existing
    // one (runtimes\runtimes) instead of merging. robocopy merges and retries files still locked
    // while the process exits. No /MIR, which would delete the uninstaller (unins*.exe) and anything
    // else beside the app.
    //
    // The install is backed up first and restored on failure, the exe is copied last, and the
    // launcher only restarts when the folder holds one whole version. On failure the log goes to
    // -Report for the next start to show. Keep this ASCII: Windows PowerShell reads a script without
    // a BOM in the ANSI code page.
    private const string UpdaterScript = """
        param(
          [int]$LauncherPid,
          [string]$Source,
          [string]$Dest,
          [string]$Exe,
          [string]$Report
        )
        $ErrorActionPreference = 'SilentlyContinue'
        $work = [System.IO.Path]::GetDirectoryName($PSCommandPath)
        $log = Join-Path $work 'apply-update.log'
        $backup = Join-Path $work 'backup'
        function Log($m) { "{0:o}  {1}" -f (Get-Date), $m | Out-File -FilePath $log -Append -Encoding utf8 }

        # robocopy: exit codes 0-7 are success (bit flags), 8+ mean a copy failure.
        function Copy-Tree([string]$From, [string]$To, [string[]]$Options, [string]$Step) {
          for ($attempt = 1; $attempt -le 5; $attempt++) {
            & robocopy $From $To @Options /R:3 /W:2 /NFL /NDL /NP /NJH /NJS | Out-Null
            $code = $LASTEXITCODE
            Log "$Step attempt $attempt exit $code"
            if ($code -lt 8) { return $true }
            Start-Sleep -Seconds 1
          }
          return $false
        }

        function Stop-Update([string]$Why, [bool]$Relaunch) {
          Log $Why
          if ($Report) {
            New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($Report)) | Out-Null
            Copy-Item -LiteralPath $log -Destination $Report -Force
          }
          if ($Relaunch) { Start-Process -FilePath $Exe -WorkingDirectory $Dest }
          exit 1
        }

        try { Wait-Process -Id $LauncherPid -Timeout 120 } catch {}
        if (Get-Process -Id $LauncherPid -ErrorAction SilentlyContinue) {
          Stop-Update 'the launcher did not exit, so nothing was changed' $false
        }
        Start-Sleep -Seconds 1

        Remove-Item -LiteralPath $backup -Recurse -Force
        if (-not (Copy-Tree $Dest $backup @('/E') 'backup')) {
          Stop-Update 'the current install could not be backed up, so nothing was changed' $true
        }

        $exeName = [System.IO.Path]::GetFileName($Exe)
        $ok = Copy-Tree $Source $Dest @('/E', '/XF', $exeName) 'copy'
        if ($ok) { $ok = Copy-Tree $Source $Dest @($exeName) 'copy exe' }
        if (-not $ok) {
          if (Copy-Tree $backup $Dest @('/E') 'restore') {
            Stop-Update 'the new files could not be copied; the previous version was put back' $true
          }
          Stop-Update 'the new files could not be copied and the previous version could not be put back' $false
        }

        Log "copy ok; relaunching $Exe"
        Remove-Item -LiteralPath $backup -Recurse -Force
        if ($Report) { Remove-Item -LiteralPath $Report -Force }
        Start-Process -FilePath $Exe -WorkingDirectory $Dest
        """;
}
