using CloudLauncher.Server.Storage;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudLauncher.Server.Controllers;

/// <summary>
/// The Linux launcher's releases: the feed its updater reads, the package it downloads, and the
/// website's Linux download. Anonymous, like the Windows routes; published by copying files into the
/// launcher root's <c>linux</c> folder (tools/release/publish-linux.py), never uploaded.
/// </summary>
/// <remarks>The package is a .tar.gz signed exactly like the Windows zip (<c>Manifest</c>,
/// <c>Signature</c>, <c>KeyId</c> in the release record), so the same verifier checks it.</remarks>
[ApiController]
public class LinuxLauncherController(LinuxLauncherStore store) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("launcher/linux/latest")]
    public ActionResult<LauncherReleaseInfo> Latest()
    {
        Response.Headers.CacheControl = "no-store";
        var info = store.GetLatest();
        return info is null ? NoContent() : Ok(info);
    }

    /// <summary>The Linux release history, newest first, for the changelog.</summary>
    [AllowAnonymous]
    [HttpGet("launcher/linux/releases")]
    public ActionResult<IReadOnlyList<LauncherReleaseInfo>> Releases()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(store.GetHistory());
    }

    /// <summary>The current package, or with <c>?sha256=</c> exactly that package (or 404), the way the
    /// Windows updater fetches the build it verified.</summary>
    [AllowAnonymous]
    [HttpGet("launcher/linux/download")]
    public IActionResult Download()
    {
        if (Request.Query.TryGetValue("sha256", out var requested))
        {
            var sha256 = requested.Count == 1 ? requested[0] : null;
            if (!LauncherStore.IsSha256Hex(sha256)) return BadRequest(new { error = "sha256 must be 64 hex digits." });
            var hash = sha256!.ToLowerInvariant();
            var byHash = store.OpenPackageBySha256(hash);
            if (byHash is null) return NotFound();
            var name = store.FindRelease(hash)?.FileName is { Length: > 0 } known ? known : "CloudLauncher-linux-x64.tar.gz";
            return File(byHash, "application/gzip", name, enableRangeProcessing: true);
        }
        return Package();
    }

    /// <summary>The website's Linux download: the current release's .tar.gz under its own name.</summary>
    [AllowAnonymous]
    [HttpGet("/download/linux")]
    public IActionResult WebsiteDownload()
    {
        Response.Headers.CacheControl = "no-store";
        return Package();
    }

    private IActionResult Package()
    {
        var info = store.GetLatest();
        var stream = store.OpenPackage();
        if (info is null || stream is null)
        {
            stream?.Dispose();
            return NotFound();
        }
        return File(stream, "application/gzip", info.FileName, enableRangeProcessing: true);
    }
}
