using CloudLauncher.Server.Storage;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;

namespace CloudLauncher.Server.Controllers;

/// <summary>Distributes launcher self-updates. Any client (even logged-out) can query the
/// latest version and download the package; only admins can publish a new build, and only when
/// uploads are switched on.</summary>
[ApiController]
[Route("launcher")]
public class LauncherController(LauncherStore store) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("latest")]
    public ActionResult<LauncherReleaseInfo> Latest()
    {
        Response.Headers.CacheControl = "no-store";
        var info = store.GetLatest();
        return info is null ? NoContent() : Ok(AsSeenByCaller(info));
    }

    /// <summary>The published release history, newest first, shown as the launcher's changelog.
    /// Anonymous like <see cref="Latest"/>, so people can read it before installing.</summary>
    [AllowAnonymous]
    [HttpGet("releases")]
    public ActionResult<IReadOnlyList<LauncherReleaseInfo>> Releases()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(store.GetHistory().Select(AsSeenByCaller).ToList());
    }

    /// <summary>
    /// A release as the asking launcher should see it. Version numbers restarted at 0.8.4 after
    /// 1.8.3, so the old line's 1.a.b is the new line's 0.a.b. Old-line launchers only update to a
    /// higher number, so they see 0.x releases as 1.x (0.8.4 as 1.8.4). Everyone else sees the
    /// real numbers.
    /// </summary>
    /// <remarks>Old-line launchers are the callers without a User-Agent: launchers since 0.8.4 send
    /// <c>CloudLauncher/0.x</c> and browsers always send one. The next major after 0.x is 2.0, which
    /// already reads as newer. The launcher side of this mapping is <c>AppVersion.Rank</c>. Only
    /// <c>Version</c> changes; the signature fields are carried over.</remarks>
    private LauncherReleaseInfo AsSeenByCaller(LauncherReleaseInfo info)
    {
        if (!string.IsNullOrEmpty(Request.Headers.UserAgent.ToString())) return info;
        if (!Version.TryParse(info.Version, out var v) || v.Major != 0) return info;
        return info with { Version = $"1.{v.Minor}.{Math.Max(v.Build, 0)}" };
    }

    /// <summary>Serves the self-update package (zip) for the in-app updater. With <c>?sha256=</c> it
    /// serves the package with that hash or 404: packages/{sha256}.bin when the publish kept one,
    /// otherwise package.bin only while it is the current release's.</summary>
    [AllowAnonymous]
    [HttpGet("download")]
    public IActionResult Download()
    {
        if (Request.Query.TryGetValue("sha256", out var requested))
            return DownloadBySha256(requested.Count == 1 ? requested[0] : null);

        var info = store.GetLatest();
        var stream = store.OpenPackage();
        if (info is null || stream is null)
        {
            stream?.Dispose();
            return NotFound();
        }

        return File(stream, "application/octet-stream", info.FileName, enableRangeProcessing: true);
    }

    private IActionResult DownloadBySha256(string? sha256)
    {
        // Checked before the value is used in any path.
        if (!LauncherStore.IsSha256Hex(sha256))
            return BadRequest(new { error = "sha256 must be 64 hex digits." });

        var hash = sha256!.ToLowerInvariant();
        var stream = store.OpenPackageBySha256(hash);
        if (stream is null) return NotFound();

        var name = store.FindRelease(hash)?.FileName is { Length: > 0 } known ? known : "CloudLauncher.zip";
        return File(stream, "application/zip", name, enableRangeProcessing: true);
    }

    /// <summary>Serves the Windows setup .exe for the website's Download button, at the top-level
    /// <c>/download</c> path. 404 if the latest release was published without an installer.</summary>
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

    /// <summary>Publishes a build from an upload. The route only exists while
    /// <c>Launcher:AllowUpload</c> is on; otherwise it is a 404 before sign-in or the body is
    /// looked at.</summary>
    [UploadEnabled]
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
        if (!store.AllowUpload) return NotFound();
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

    /// <summary>Lets the action match only while <see cref="LauncherStoreOptions.AllowUpload"/> is on.
    /// A constraint rather than a check in the action, because routing runs before authorization
    /// and model binding: a turned-off route is a plain 404 for everyone.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    private sealed class UploadEnabledAttribute : Attribute, IActionConstraint
    {
        public int Order => 0;

        public bool Accept(ActionConstraintContext context) =>
            context.RouteContext.HttpContext.RequestServices.GetService<LauncherStoreOptions>()?.AllowUpload == true;
    }
}
