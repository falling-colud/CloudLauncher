using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>Checks the server for a newer launcher build and applies it.
///
/// Because a running executable can't overwrite itself, applying an update works by
/// downloading the published package (a zip of the launcher's publish output), extracting
/// it to a temp staging folder, then launching a small PowerShell script that waits for this
/// process to exit, copies the new files over the install directory, and relaunches the app.</summary>
public sealed class UpdateService(ApiClient api)
{
    public Task<LauncherReleaseInfo?> GetLatestAsync(CancellationToken ct = default) =>
        api.GetLatestLauncherAsync(ct);

    /// <summary>Returns the latest release if it is newer than the running build, otherwise null.
    /// Swallows network errors so callers can use it as a best-effort check on launch.</summary>
    public async Task<LauncherReleaseInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var latest = await api.GetLatestLauncherAsync(ct);
            return latest is not null && AppVersion.IsNewer(latest.Version) ? latest : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Downloads and stages the update, launches the external updater, and signals the
    /// caller that it should shut the application down so the files can be replaced.</summary>
    public async Task DownloadAndApplyAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var work = Path.Combine(Path.GetTempPath(), "CloudLauncherUpdate");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        var zipPath = Path.Combine(work, "package.zip");
        using (var resp = await api.DownloadLauncherAsync(ct))
        {
            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0)
                    progress?.Report((double)read / total.Value);
            }
        }

        var staging = Path.Combine(work, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, staging);

        // If the archive wraps everything in a single top-level folder, copy from inside it.
        var sourceRoot = staging;
        var topDirs = Directory.GetDirectories(staging);
        var topFiles = Directory.GetFiles(staging);
        if (topFiles.Length == 0 && topDirs.Length == 1)
            sourceRoot = topDirs[0];

        var installDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var exePath = Process.GetCurrentProcess().MainModule?.FileName
                      ?? Path.Combine(installDir, "CloudLauncher.exe");
        var pid = Environment.ProcessId;

        var scriptPath = Path.Combine(work, "apply-update.ps1");
        await File.WriteAllTextAsync(scriptPath, UpdaterScript, ct);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" " +
                        $"-LauncherPid {pid} -Source \"{sourceRoot}\" -Dest \"{installDir}\" -Exe \"{exePath}\"",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        };
        Process.Start(psi);
    }

    // Waits for the launcher to exit, copies the new build over the install directory, then
    // relaunches. Kept dependency-free so it runs on a stock Windows install (robocopy ships
    // with every supported Windows).
    //
    // We use robocopy rather than Copy-Item because `Copy-Item -Path Source\* -Dest Dest -Recurse`
    // has a well-known footgun: when a subfolder (e.g. "runtimes") already exists in the install
    // directory — which is always the case for an update over an existing install — it copies the
    // source subfolder *into* the existing one (runtimes\runtimes) instead of merging. robocopy
    // merges correctly and retries files that are briefly still locked as the process exits.
    // No /MIR: mirroring would delete the installer's uninstaller (unins*.exe) and anything else
    // living alongside the app, so we copy additively.
    private const string UpdaterScript = """
        param(
          [int]$LauncherPid,
          [string]$Source,
          [string]$Dest,
          [string]$Exe
        )
        $ErrorActionPreference = 'SilentlyContinue'
        $log = Join-Path ([System.IO.Path]::GetDirectoryName($PSCommandPath)) 'apply-update.log'
        function Log($m) { "{0:o}  {1}" -f (Get-Date), $m | Out-File -FilePath $log -Append -Encoding utf8 }

        try { Wait-Process -Id $LauncherPid -Timeout 120 } catch {}
        Start-Sleep -Seconds 1

        $ok = $false
        for ($attempt = 1; $attempt -le 5; $attempt++) {
          & robocopy $Source $Dest /E /R:3 /W:2 /NFL /NDL /NP /NJH /NJS | Out-Null
          $code = $LASTEXITCODE
          Log "robocopy attempt $attempt exit $code"
          # robocopy: exit codes 0-7 are success (bit flags), 8+ mean a copy failure.
          if ($code -lt 8) { $ok = $true; break }
          Start-Sleep -Seconds 1
        }
        Log "copy ok=$ok; relaunching $Exe"
        Start-Process -FilePath $Exe -WorkingDirectory $Dest
        """;
}
