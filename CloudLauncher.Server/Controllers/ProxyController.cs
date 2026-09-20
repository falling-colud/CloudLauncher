using System.Text;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Net;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

/// <summary>Transparent proxy for upstream mod-platform APIs (CurseForge, Modrinth)
/// that injects the admin's API key server-side. Clients never see the key — they
/// just call /proxy/{platform}/{path} with the same payload they'd send upstream.
///
/// The call is attempted over each configured route in turn (see <see cref="UpstreamRouter"/>):
/// if a platform's CDN refuses this server's address, the same request is retried through a relay
/// or outbound proxy instead of failing. Configure routes under "Upstream" — with none configured
/// there is exactly one route, the direct one, and this behaves as it always did.</summary>
[ApiController]
[Authorize]
[Route("proxy/{platform}/{**relativePath}")]
public class ProxyController(UpstreamRouter router, UpstreamGuard guard, AppDbContext db, ILogger<ProxyController> log) : ControllerBase
{
    /// <summary>Header a launcher sends its own CurseForge key in. Mirrors
    /// <c>ApiClient.OwnCurseForgeKeyHeader</c> on the client.</summary>
    public const string OwnKeyHeader = "X-CloudLauncher-CF-Key";

    /// <summary>List of HTTP methods we forward. Anything else is rejected.</summary>
    private static readonly HashSet<string> AllowedMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "POST" };

    /// <summary>Cap on a POST body held in memory so a blocked route can be retried on the next one.
    /// The calls that POST through here are small (fingerprint and id lists). A larger body is
    /// streamed straight through on the first route and simply does not get a second attempt —
    /// keeping the memory-pressure lever this proxy had at the 200 MB request cap.</summary>
    private const int MaxReplayableBodyBytes = 1024 * 1024;

    /// <summary>Cap on an error body buffered to tell a CDN block page from a real API error.
    /// The CloudFront page is about a kilobyte; anything past the cap is streamed on unexamined.</summary>
    private const int ErrorSniffBytes = 64 * 1024;

    [AcceptVerbs("GET", "POST")]
    public async Task<IActionResult> Forward(string platform, string? relativePath, CancellationToken ct)
    {
        if (!AllowedMethods.Contains(Request.Method))
            return StatusCode(StatusCodes.Status405MethodNotAllowed);

        var routes = router.RoutesFor(platform);
        if (routes.Count == 0)
            return BadRequest(new { error = $"Unknown proxy platform '{platform}'" });

        var (keyHeader, keyValue, requiresKey, ownKey) = await ResolveKeyAsync(platform, ct);
        if (requiresKey && string.IsNullOrWhiteSpace(keyValue))
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = $"{DisplayName(platform)} API key not configured. An admin must set it in the dev menu. " +
                              "You can also set your own key in Settings → Mod stores." });

        // Everything the guard paces, pauses and counts is per *key*, not per platform: a user on
        // their own CurseForge key has their own quota upstream, so queueing them behind the shared
        // key's burst (or pausing them when it gets throttled) would throw away the point of setting
        // one. The response cache stays keyed by platform — the data is identical either way.
        var bucket = ownKey ? guard.BucketFor(platform, keyValue) : platform;

        var path = (relativePath ?? "").TrimStart('/');
        var query = Request.QueryString.HasValue ? Request.QueryString.Value : "";

        // Identical GETs from many launchers (the same mod's version list during everyone's update
        // check) are answered from a short cache instead of each going upstream.
        var cacheTtl = HttpMethods.IsGet(Request.Method) ? UpstreamGuard.CacheTtl(platform, path + query) : null;
        var cacheKey = UpstreamGuard.CacheKey(platform, path + query);
        if (cacheTtl is not null && guard.TryGetCached(cacheKey, out var cached))
        {
            Response.StatusCode = cached.StatusCode;
            Response.ContentType = cached.ContentType;
            Response.Headers["X-CloudLauncher-Cache"] = "hit";
            await Response.Body.WriteAsync(cached.Body, ct);
            return new EmptyResult();
        }

        // Buffer a small POST body up front: once it has been streamed to a route that turns out to
        // be blocked, there is nothing left to replay on the next one.
        var isPost = HttpMethods.IsPost(Request.Method);
        var replayBody = isPost ? await ReadReplayableBodyAsync(ct) : null;
        var singleAttempt = isPost && replayBody is null;

        var tried = new List<ResolvedRoute>();
        string? blockedDetail = null;
        string? blockedRequestId = null;
        string? transportDetail = null;

        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            tried.Add(route);
            var canRetry = !singleAttempt && i + 1 < routes.Count;
            var upstreamUrl = $"{route.BaseUrl}/{path}{query}";

            using var req = new HttpRequestMessage(new HttpMethod(Request.Method), upstreamUrl);
            if (!string.IsNullOrWhiteSpace(keyHeader) && !string.IsNullOrWhiteSpace(keyValue))
                req.Headers.TryAddWithoutValidation(keyHeader, keyValue);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "CloudLauncher/1.0");
            foreach (var (name, value) in route.Headers)
                req.Headers.TryAddWithoutValidation(name, value);

            if (isPost)
            {
                HttpContent content = replayBody is not null
                    ? new ByteArrayContent(replayBody)
                    : new StreamContent(Request.Body);
                var contentType = Request.ContentType;
                content.Headers.TryAddWithoutValidation(
                    "Content-Type", string.IsNullOrEmpty(contentType) ? "application/json" : contentType);
                req.Content = content;
            }

            // Bounded and spaced per platform: a burst from many launchers queues here rather than
            // arriving at the store all at once. A queue too long to wait out is refused instead.
            var refusedFor = await guard.TryAcquireAsync(bucket, ct);
            if (refusedFor is { } queueWait)
                return RateLimited(platform, queueWait,
                    $"{DisplayName(platform)} requests from this server are queued to stay inside its rate limit. " +
                    "Try again in a moment.");

            HttpResponseMessage resp;
            try
            {
                resp = await router.ClientFor(route).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // the caller hung up; not this route's fault
            }
            catch (Exception ex)
            {
                // Includes the client-side timeout, which for our purposes is the same as a refusal.
                transportDetail = $"{route.Name}: {ex.Message}";
                router.MarkBlocked(route, ex.Message);
                if (canRetry) continue;
                return StatusCode(StatusCodes.Status502BadGateway,
                    new { error = "Upstream request failed", detail = transportDetail });
            }
            finally
            {
                guard.Release(bucket);
            }

            // Dispose the response (and thus return the pooled connection promptly) once the body
            // has been streamed through. Without this the message leaks under proxy load.
            try
            {
                var upstream = await resp.Content.ReadAsStreamAsync(ct);
                var head = Array.Empty<byte>();

                if (!resp.IsSuccessStatusCode)
                {
                    head = await ReadHeadAsync(upstream, ErrorSniffBytes, ct);
                    var text = Encoding.UTF8.GetString(head);
                    if (UpstreamEdge.IsBlockPage(text))
                    {
                        blockedRequestId ??= UpstreamEdge.RequestId(text);
                        blockedDetail = $"{route.Name}: {(int)resp.StatusCode} edge block";
                        router.MarkBlocked(route, $"CDN block page, HTTP {(int)resp.StatusCode}");
                        if (canRetry)
                        {
                            log.LogInformation(
                                "{Platform} refused route '{Route}'; retrying on '{Next}'.",
                                DisplayName(platform), route.Name, routes[i + 1].Name);
                            continue;
                        }
                        return StatusCode(StatusCodes.Status502BadGateway, new
                        {
                            error = EdgeBlockMessage(platform, tried, routes, blockedRequestId),
                            code = "upstream_blocked",
                            requestId = blockedRequestId
                        });
                    }
                }

                // Anything the API itself produced — a 404 or a rejected key included — proves the
                // request got through, so the route counts as working again.
                router.MarkHealthy(route);

                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    // The store is counting our shared address: hold every send to it for the
                    // requested time, and tell the client the same so it waits rather than retries.
                    var delay = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                    guard.BackOff(bucket, Clamp(delay));
                    log.LogWarning("{Platform} answered 429 on route '{Route}'; holding sends to it for {Delay}s.",
                        DisplayName(platform), route.Name, (int)Clamp(delay).TotalSeconds);
                    return RateLimited(platform, Clamp(delay),
                        $"{DisplayName(platform)} is rate-limiting the launcher server right now. Try again in a moment.");
                }

                // CurseForge throttles a burst with fast 403s rather than 429s (seen 2026-09-12: ~8/s
                // refused for minutes, one request in twenty seconds let through). A 403 moments after
                // the same key was answering 200 is that throttle, not a revoked key: pause and say so.
                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden
                    && platform.Equals("curseforge", StringComparison.OrdinalIgnoreCase)
                    && guard.SucceededRecently(bucket))
                {
                    guard.BackOff(bucket, UpstreamGuard.ThrottlePause);
                    log.LogWarning("CurseForge answered 403 on route '{Route}' while the key was working minutes ago; " +
                                   "treating it as throttling and pausing CurseForge calls for {Delay}s.",
                        route.Name, (int)UpstreamGuard.ThrottlePause.TotalSeconds);
                    return RateLimited(platform, UpstreamGuard.ThrottlePause,
                        ownKey
                            ? "CurseForge is temporarily refusing requests made with your API key (too many in a short " +
                              "time). This is not the key itself. Try again in a minute."
                            : "CurseForge is temporarily refusing requests from the launcher server (too many in a short " +
                              "time). This is not the API key. Try again in a minute.");
                }

                // A 403 on a key that has never answered here is the key, and when it is the user's
                // own key they are the only one who can fix it — so name it rather than leaving them
                // to read "the server's key was rejected" about a key they set themselves.
                if (ownKey && resp.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
                    return StatusCode(StatusCodes.Status400BadRequest, new
                    {
                        error = "CurseForge rejected the API key you set in Settings → Mod stores. Check it, or clear " +
                                "the box to go back to the launcher's shared key.",
                        code = "own_key_rejected"
                    });

                if (resp.IsSuccessStatusCode) guard.NoteSuccess(bucket);

                Response.StatusCode = (int)resp.StatusCode;
                Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";

                // A cacheable success: buffer it (bounded), remember it, then send it.
                if (cacheTtl is not null && resp.IsSuccessStatusCode
                    && (resp.Content.Headers.ContentLength ?? 0) <= UpstreamGuard.MaxCacheableBytes)
                {
                    var (body, complete) = await ReadUpToAsync(upstream, head, UpstreamGuard.MaxCacheableBytes, ct);
                    if (complete)
                        guard.Store(cacheKey,
                            new UpstreamGuard.CachedResponse((int)resp.StatusCode, Response.ContentType, body),
                            cacheTtl.Value);
                    await Response.Body.WriteAsync(body, ct);
                    if (!complete) await upstream.CopyToAsync(Response.Body, ct); // too big to keep: stream the rest
                    return new EmptyResult();
                }

                if (head.Length > 0) await Response.Body.WriteAsync(head, ct);
                await upstream.CopyToAsync(Response.Body, ct);
                return new EmptyResult();
            }
            finally
            {
                resp.Dispose();
            }
        }

        // Unreachable: the final iteration always returns rather than continuing. Kept because the
        // compiler cannot see that, and a clear answer beats a surprise if the loop ever changes.
        return StatusCode(StatusCodes.Status502BadGateway, new
        {
            error = blockedDetail is not null
                ? EdgeBlockMessage(platform, tried, routes, blockedRequestId)
                : transportDetail ?? "Upstream request failed",
            code = blockedDetail is not null ? "upstream_blocked" : "upstream_unreachable"
        });
    }

    /// <summary>The admin-facing explanation for a block that no route got around. It reports the
    /// routes actually attempted, not the ones configured: "we are blocked", "your relay is blocked
    /// too" and "we never got to your relay" call for three different next steps.</summary>
    private static string EdgeBlockMessage(
        string platform,
        IReadOnlyList<ResolvedRoute> tried,
        IReadOnlyList<ResolvedRoute> configured,
        string? requestId)
    {
        var name = DisplayName(platform);
        var suffix = requestId is null ? "" : $" (CloudFront request {requestId})";
        var lead = $"{name}'s CDN is blocking this server, so the request never reached the API. " +
                   "This is not the API key — no key, valid or not, changes it.";

        if (configured.Count == 1)
            return lead + $" Route {name} traffic through a host that is not blocked by setting " +
                          $"Upstream:{platform} (see deploy/UPSTREAM-ROUTING.md), or get this server's " +
                          $"address unblocked.{suffix}";

        var refused = $" Refused on {string.Join(", ", tried.Select(r => r.Name))}.";
        var rest = tried.Count < configured.Count
            // Only happens for a POST body too large to hold for a replay, so there was nothing
            // left to send to the remaining routes.
            ? " The remaining routes were not tried, because this request's body was too large to replay."
            : " So the alternate host is blocked too.";
        return lead + refused + rest + $" See deploy/UPSTREAM-ROUTING.md.{suffix}";
    }

    private static TimeSpan Clamp(TimeSpan delay) =>
        delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
        : delay > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2)
        : delay;

    /// <summary>A 429 in the launcher's own JSON error shape, with a Retry-After the client honours.</summary>
    private ObjectResult RateLimited(string platform, TimeSpan retryAfter, string message)
    {
        Response.Headers["Retry-After"] = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            error = message,
            code = "upstream_rate_limited",
            platform = DisplayName(platform),
            retryAfterSeconds = (int)Math.Ceiling(retryAfter.TotalSeconds)
        });
    }

    private static string DisplayName(string platform) => platform.ToLowerInvariant() switch
    {
        "curseforge" => "CurseForge",
        "modrinth"   => "Modrinth",
        _            => platform
    };

    /// <summary>The POST body, buffered so it can be replayed on a second route — or null when it is
    /// too large (or of unknown length) to hold, in which case the request gets one attempt.</summary>
    private async Task<byte[]?> ReadReplayableBodyAsync(CancellationToken ct)
    {
        var length = Request.ContentLength;
        if (length is null or > MaxReplayableBodyBytes) return null;

        using var buffer = new MemoryStream((int)length.Value);
        await Request.Body.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>Reads the rest of <paramref name="stream"/> after an already-read <paramref name="head"/>,
    /// stopping once past <paramref name="cap"/> bytes. <c>Complete</c> is false when the body was
    /// larger — the caller then streams the remainder instead of caching.</summary>
    private static async Task<(byte[] Body, bool Complete)> ReadUpToAsync(
        Stream stream, byte[] head, int cap, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        if (head.Length > 0) buffer.Write(head);
        var chunk = new byte[64 * 1024];
        while (buffer.Length <= cap)
        {
            var n = await stream.ReadAsync(chunk, ct);
            if (n == 0) return (buffer.ToArray(), true);
            buffer.Write(chunk, 0, n);
        }
        return (buffer.ToArray(), false);
    }

    private static async Task<byte[]> ReadHeadAsync(Stream stream, int cap, CancellationToken ct)
    {
        var buffer = new byte[cap];
        var read = 0;
        while (read < cap)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, cap - read), ct);
            if (n == 0) break;
            read += n;
        }
        return read == cap ? buffer : buffer[..read];
    }

    /// <summary>Longest caller-supplied key accepted, so a header cannot be used to push arbitrary
    /// data upstream. CurseForge keys are ~60 characters.</summary>
    private const int MaxOwnKeyLength = 200;

    private async Task<(string KeyHeader, string? KeyValue, bool RequiresKey, bool OwnKey)> ResolveKeyAsync(
        string platform, CancellationToken ct)
    {
        // A caller may bring their own CurseForge key (Settings → Mod stores). It is used for this
        // request only: never written to the database, never logged, never handed to another user.
        if (platform.Equals("curseforge", StringComparison.OrdinalIgnoreCase)
            && Request.Headers.TryGetValue(OwnKeyHeader, out var supplied)
            && supplied.ToString() is { Length: > 0 } own
            && own.Length <= MaxOwnKeyLength
            && own.All(c => c is > (char)0x20 and < (char)0x7F))
        {
            return ("x-api-key", own, true, true);
        }

        // Read once a minute rather than once per proxied call (SettingsController drops the cache
        // when an admin changes a key).
        var (curseForgeKey, modrinthToken) = await guard.GetKeysAsync(async () =>
        {
            var settings = await db.GlobalSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            return (settings?.CurseForgeApiKey, settings?.ModrinthToken);
        });
        return platform.ToLowerInvariant() switch
        {
            "curseforge" => ("x-api-key",     curseForgeKey, true,  false),
            "modrinth"   => ("Authorization", modrinthToken, false, false),
            _            => ("", null, false, false)
        };
    }
}
