using CloudLauncher.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

/// <summary>Transparent proxy for upstream mod-platform APIs (CurseForge, Modrinth)
/// that injects the admin's API key server-side. Clients never see the key — they
/// just call /proxy/{platform}/{path} with the same payload they'd send upstream.</summary>
[ApiController]
[Authorize]
[Route("proxy/{platform}/{**relativePath}")]
public class ProxyController(IHttpClientFactory http, AppDbContext db) : ControllerBase
{
    /// <summary>List of HTTP methods we forward. Anything else is rejected.</summary>
    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "POST" };

    [AcceptVerbs("GET", "POST")]
    public async Task<IActionResult> Forward(string platform, string? relativePath, CancellationToken ct)
    {
        if (!AllowedMethods.Contains(Request.Method))
            return StatusCode(StatusCodes.Status405MethodNotAllowed);

        var (baseUrl, keyHeader, keyValue, requiresKey) = await ResolveUpstreamAsync(platform, ct);
        if (baseUrl is null)
            return BadRequest(new { error = $"Unknown proxy platform '{platform}'" });
        if (requiresKey && string.IsNullOrWhiteSpace(keyValue))
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = $"{platform} API key not configured. An admin must set it in the dev menu." });

        var path = (relativePath ?? "").TrimStart('/');
        var query = Request.QueryString.HasValue ? Request.QueryString.Value : "";
        var upstream = $"{baseUrl}/{path}{query}";

        var client = http.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(2);

        using var req = new HttpRequestMessage(new HttpMethod(Request.Method), upstream);
        if (!string.IsNullOrWhiteSpace(keyHeader) && !string.IsNullOrWhiteSpace(keyValue))
            req.Headers.TryAddWithoutValidation(keyHeader, keyValue);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "CloudLauncher/1.0");

        if (HttpMethods.IsPost(Request.Method))
        {
            // Buffer the request body so we can hand it to HttpClient.
            using var ms = new MemoryStream();
            await Request.Body.CopyToAsync(ms, ct);
            ms.Position = 0;
            var content = new ByteArrayContent(ms.ToArray());
            var ct2 = Request.ContentType;
            if (!string.IsNullOrEmpty(ct2))
                content.Headers.TryAddWithoutValidation("Content-Type", ct2);
            else
                content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            req.Content = content;
        }

        HttpResponseMessage resp;
        try { resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Upstream request failed", detail = ex.Message });
        }

        // Dispose the response (and thus return the pooled connection promptly) once the body
        // has been streamed through. Without this the message leaks under proxy load.
        using (resp)
        {
            Response.StatusCode = (int)resp.StatusCode;
            Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await stream.CopyToAsync(Response.Body, ct);
        }
        return new EmptyResult();
    }

    private async Task<(string? BaseUrl, string KeyHeader, string? KeyValue, bool RequiresKey)> ResolveUpstreamAsync(
        string platform, CancellationToken ct)
    {
        var settings = await db.GlobalSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return platform.ToLowerInvariant() switch
        {
            "curseforge" => ("https://api.curseforge.com/v1", "x-api-key",     settings?.CurseForgeApiKey, true),
            "modrinth"   => ("https://api.modrinth.com/v2",   "Authorization", settings?.ModrinthToken,    false),
            _            => (null, "", null, false)
        };
    }
}
