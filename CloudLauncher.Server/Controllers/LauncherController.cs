using CloudLauncher.Server.Storage;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudLauncher.Server.Controllers;

/// <summary>Distributes launcher self-updates. Any client (even logged-out) can query the
/// latest version and download the package; only admins can publish a new build.</summary>
[ApiController]
[Route("launcher")]
public class LauncherController(LauncherStore store) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("latest")]
    public ActionResult<LauncherReleaseInfo> Latest()
    {
        var info = store.GetLatest();
        return info is null ? NoContent() : Ok(info);
    }

    /// <summary>The published release history, newest first — what the launcher shows as its
    /// changelog. Anonymous, like <see cref="Latest"/>: it is public information, and someone
    /// deciding whether to install the thing should be able to read what changed.</summary>
    [AllowAnonymous]
    [HttpGet("releases")]
    public ActionResult<IReadOnlyList<LauncherReleaseInfo>> Releases() => Ok(store.GetHistory());

    /// <summary>Serves the self-update package (zip) fetched by the in-app updater.</summary>
    [AllowAnonymous]
    [HttpGet("download")]
    public IActionResult Download()
    {
        var info = store.GetLatest();
        var stream = store.OpenPackage();
        if (info is null || stream is null)
        {
            stream?.Dispose();
            return NotFound();
        }

        return File(stream, "application/octet-stream", info.FileName, enableRangeProcessing: true);
    }

    /// <summary>Serves the Windows setup .exe to human downloaders (the website's Download button).
    /// Exposed at the friendly top-level <c>/download</c> path. Falls back to 404 if the latest
    /// release was published without an installer.</summary>
    [AllowAnonymous]
    [HttpGet("/download")]
    public IActionResult Installer()
    {
        var info = store.GetLatest();
        var stream = store.OpenInstaller();
        if (info is null || stream is null)
        {
            stream?.Dispose();
            return NotFound();
        }

        var name = string.IsNullOrWhiteSpace(info.InstallerFileName)
            ? $"CloudLauncher-Setup-{info.Version}.exe"
            : info.InstallerFileName;
        // Content-Disposition: attachment so browsers download rather than try to run/preview it.
        return File(stream, "application/octet-stream", name, enableRangeProcessing: true);
    }

    [Authorize(Roles = "admin")]
    [HttpPost("upload")]
    [DisableRequestSizeLimit]
    public async Task<ActionResult<LauncherReleaseInfo>> Upload(
        [FromForm] IFormFile? file,
        [FromForm] string? version,
        [FromForm] string? notes,
        [FromForm] IFormFile? installer,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A package file is required." });
        if (string.IsNullOrWhiteSpace(version))
            return BadRequest(new { error = "A version is required." });
        if (!Version.TryParse(version.Trim(), out _))
            return BadRequest(new { error = "Version must look like 1.2.3." });

        await using var s = file.OpenReadStream();
        Stream? installerStream = null;
        try
        {
            if (installer is not null && installer.Length > 0)
                installerStream = installer.OpenReadStream();

            var info = await store.StoreAsync(
                s, version.Trim(), file.FileName,
                string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                installerStream, installer?.FileName, ct);
            return Ok(info);
        }
        finally
        {
            if (installerStream is not null) await installerStream.DisposeAsync();
        }
    }
}
