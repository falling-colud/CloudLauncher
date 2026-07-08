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

    [Authorize(Roles = "admin")]
    [HttpPost("upload")]
    [DisableRequestSizeLimit]
    public async Task<ActionResult<LauncherReleaseInfo>> Upload(
        [FromForm] IFormFile? file,
        [FromForm] string? version,
        [FromForm] string? notes,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A package file is required." });
        if (string.IsNullOrWhiteSpace(version))
            return BadRequest(new { error = "A version is required." });
        if (!Version.TryParse(version.Trim(), out _))
            return BadRequest(new { error = "Version must look like 1.2.3." });

        await using var s = file.OpenReadStream();
        var info = await store.StoreAsync(
            s, version.Trim(), file.FileName,
            string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(), ct);
        return Ok(info);
    }
}
