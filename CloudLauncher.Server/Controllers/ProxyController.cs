using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CloudLauncher.Server.Data;
using CloudLauncher.Server.Net;
using CloudLauncher.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Controllers;

/// <summary>Proxy for the mod-platform APIs (CurseForge, Modrinth). CurseForge calls get the admin's
/// API key attached server-side so clients never see it. Clients call /proxy/{platform}/{path} with
/// the payload they'd send upstream; only calls in <see cref="ProxyAllowlist"/> are forwarded.</summary>
/// <remarks>Each configured route is tried in turn (see <see cref="UpstreamRouter"/>), so if a CDN
/// refuses this server's address the request goes through a relay or outbound proxy instead. Routes
/// are configured under "Upstream"; with none there is one direct route.</remarks>
[ApiController]
[Authorize]
[Route("proxy/{platform}/{**relativePath}")]
public class ProxyController(UpstreamRouter router, UpstreamGuard guard, AppDbContext db, ILogger<ProxyController> log) : ControllerBase
{
    /// <summary>Header a launcher sends its own CurseForge key in. Mirrors
    /// <c>ApiClient.OwnCurseForgeKeyHeader</c> on the client.</summary>
    public const string OwnKeyHeader = "X-CloudLauncher-CF-Key";

    /// <summary>Largest POST body accepted. POSTs through here are fingerprint, hash and id lists of a
    /// few kilobytes. The body is held whole so a blocked route can be retried on the next.</summary>
    private const int MaxBodyBytes = 1024 * 1024;

    /// <summary>Cap on an error body buffered to tell a CDN block page from a real API error.
    /// The CloudFront page is about a kilobyte; anything past the cap is streamed on unexamined.</summary>
    private const int ErrorSniffBytes = 64 * 1024;

    [AcceptVerbs("GET", "POST")]
    public async Task<IActionResult> Forward(string platform, string? relativePath, CancellationToken ct)
    {
        if (this.UserIdOrNull() is not { } user) return Unauthorized();

        var name = ProxyAllowlist.Platform(platform);
        var path = relativePath ?? "";
        if (name is null || !ProxyAllowlist.Allows(name, Request.Method, path))
        {
            log.LogDebug("Refused proxy call {Method} {Platform}/{Path}: not a call the launcher makes.",
                Request.Method, platform, path);
            return NotFound(new { error = "Not found.", code = "not_found" });
        }

        var query = Request.QueryString.HasValue ? Request.QueryString.Value! : "";
        if (query.Length > ProxyAllowlist.MaxQueryLength)
            return StatusCode(StatusCodes.Status414UriTooLong, new { error = "Request too long.", code = "request_too_large" });

        if (guard.Users.TryTake(user) is { } rateWait)
        {
            log.LogDebug("Proxy rate limit reached for user {User}.", user);
            return RateLimited(name, rateWait,
                "Too many store requests from this account. Try again in a moment.", "too_many_requests");
        }

        var (keyValue, ownKey) = await ResolveKeyAsync(name, ct);
        if (name == "curseforge" && string.IsNullOrWhiteSpace(keyValue))
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "CurseForge API key not configured. An admin must set it in the dev menu. " +
                              "You can also set your own key in Settings > Mod stores." });

        // Pacing, pauses and success windows are per bucket: the server's key has one per platform,
        // a caller's own key one of its own once CurseForge has accepted it (until then it shares
        // the untested-key bucket). The response cache stays keyed by platform: the data is the same.
        var bucket = ownKey ? guard.BucketForOwnKey(keyValue!) : guard.SharedBucket(name);

        // Identical GETs from many launchers (the same mod's version list during everyone's update
        // check) are answered from a short cache instead of each going upstream.
        var isPost = HttpMethods.IsPost(Request.Method);
        var cacheTtl = isPost ? null : UpstreamGuard.CacheTtl(name, path + query);
        var cacheKey = UpstreamGuard.CacheKey(name, path + query);
        if (cacheTtl is not null && guard.TryGetCached(cacheKey, out var cached))
        {
            Response.StatusCode = cached.StatusCode;
            Response.ContentType = cached.ContentType;
            Response.Headers["X-CloudLauncher-Cache"] = "hit";
            await Response.Body.WriteAsync(cached.Body, ct);
            return new EmptyResult();
        }

        byte[]? body = null;
        if (isPost)
        {
            body = await ReadBodyAsync(ct);
            if (body is null)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = "Request too large.", code = "request_too_large" });
        }

        var routes = router.RoutesFor(name);
        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            var canRetry = i + 1 < routes.Count;

            using var req = new HttpRequestMessage(new HttpMethod(Request.Method), $"{route.BaseUrl}/{path}{query}");
            if (!string.IsNullOrWhiteSpace(keyValue))
                req.Headers.TryAddWithoutValidation("x-api-key", keyValue);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            foreach (var (header, value) in route.Headers)
                req.Headers.TryAddWithoutValidation(header, value);
            if (body is not null)
            {
                req.Content = new ByteArrayContent(body);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            // One account holds only a few places in a bucket's queue, so it cannot fill it for everyone.
            if (guard.Users.TryEnterQueue(user, bucket.Name) is { } busy)
            {
                log.LogDebug("Proxy queue cap reached for user {User} on {Bucket}.", user, bucket.Name);
                return RateLimited(name, busy,
                    "Too many store requests from this account at once. Try again in a moment.", "too_many_requests");
            }

            HttpResponseMessage resp;
            try
            {
                // Bounded and spaced per bucket: a burst from many launchers queues here rather than
                // arriving at the store all at once. A queue too long to wait out is refused instead.
                if (await guard.TryAcquireAsync(bucket, ct) is { } queueWait)
                    return RateLimited(name, queueWait,
                        $"{DisplayName(name)} requests from this server are queued to stay inside its rate limit. " +
                        "Try again in a moment.");
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
                    router.MarkBlocked(route, ex.Message);
                    if (canRetry) continue;
                    log.LogWarning("{Platform} could not be reached on any route ({Routes}).",
                        DisplayName(name), string.Join(", ", routes.Select(r => r.Name)));
                    return Unreachable(name);
                }
                finally
                {
                    guard.Release(bucket);
                }
            }
            finally
            {
                guard.Users.LeaveQueue(user, bucket.Name);
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
                        router.MarkBlocked(route, $"CDN block page, HTTP {(int)resp.StatusCode}");
                        if (canRetry)
                        {
                            log.LogInformation(
                                "{Platform} refused route '{Route}'; retrying on '{Next}'.",
                                DisplayName(name), route.Name, routes[i + 1].Name);
                            continue;
                        }
                        log.LogWarning("{Explanation}", EdgeBlockMessage(name, routes, UpstreamEdge.RequestId(text)));
                        return StatusCode(StatusCodes.Status502BadGateway, new
                        {
                            error = $"{DisplayName(name)} is refusing requests from the launcher server right now. Try again later.",
                            code = "upstream_blocked"
                        });
                    }
                }

                // Anything the API itself produced (a 404 or a rejected key included) proves the
                // request got through, so the route counts as working again.
                router.MarkHealthy(route);

                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    // The store is counting this key's calls: hold every send in its bucket for the
                    // requested time, and tell the client the same so it waits rather than retries.
                    var delay = Clamp(resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10));
                    if (!bucket.ManyKeys) guard.BackOff(bucket, delay);
                    log.LogWarning("{Platform} answered 429 on route '{Route}' for bucket {Bucket}; waiting {Delay}s.",
                        DisplayName(name), route.Name, bucket.Name, (int)delay.TotalSeconds);
                    return RateLimited(name, delay,
                        $"{DisplayName(name)} is rate-limiting the launcher server right now. Try again in a moment.");
                }

                // CurseForge throttles a burst with fast 403s rather than 429s, so a 403 on the shared
                // key moments after it was answering 200 is that throttle, not a revoked key: pause and
                // say so. Not for a caller's own key, which says nothing about the shared one, and not
                // for a download URL, whose 403 is usually the author's opt-out from third-party downloads.
                if (resp.StatusCode == HttpStatusCode.Forbidden
                    && name == "curseforge" && !ownKey && !IsDownloadUrl(path)
                    && guard.SucceededRecently(bucket))
                {
                    guard.BackOff(bucket, UpstreamGuard.ThrottlePause);
                    log.LogWarning("CurseForge answered 403 on route '{Route}' while the key was working minutes ago; " +
                                   "treating it as throttling and pausing CurseForge calls for {Delay}s.",
                        route.Name, (int)UpstreamGuard.ThrottlePause.TotalSeconds);
                    return RateLimited(name, UpstreamGuard.ThrottlePause,
                        "CurseForge is temporarily refusing requests from the launcher server (too many in a short " +
                        "time). This is not the API key. Try again in a minute.");
                }

                // A 401/403 on the user's own key is the key, and they are the only one who can fix it,
                // so name it rather than leaving them to read "the server's key was rejected".
                if (ownKey && resp.StatusCode is (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    && !IsDownloadUrl(path))
                    return StatusCode(StatusCodes.Status400BadRequest, new
                    {
                        error = "CurseForge rejected the API key you set in Settings > Mod stores. Check it, or clear " +
                                "the box to go back to the launcher's shared key.",
                        code = "own_key_rejected"
                    });

                if (resp.IsSuccessStatusCode)
                {
                    if (ownKey) guard.NoteOwnKeySuccess(keyValue!);
                    else guard.NoteSuccess(bucket);
                }

                Response.StatusCode = (int)resp.StatusCode;
                Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";

                // A cacheable success on the server's key: buffer it (bounded), remember it, then send
                // it. An answer fetched with a caller's own key is passed on but never kept for others.
                if (cacheTtl is not null && !ownKey && resp.IsSuccessStatusCode
                    && (resp.Content.Headers.ContentLength ?? 0) <= UpstreamGuard.MaxCacheableBytes)
                {
                    var (buffered, complete) = await ReadUpToAsync(upstream, head, UpstreamGuard.MaxCacheableBytes, ct);
                    if (complete)
                        guard.Store(cacheKey,
                            new UpstreamGuard.CachedResponse((int)resp.StatusCode, Response.ContentType, buffered),
                            cacheTtl.Value);
                    await Response.Body.WriteAsync(buffered, ct);
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

        // Only reached with no route at all; every route failing returns from inside the loop.
        return Unreachable(name);
    }

    /// <summary>The admin-facing explanation of a block no route got around. It goes to the server
    /// log only: clients get a plain sentence without route names or hosts.</summary>
    private static string EdgeBlockMessage(string platform, IReadOnlyList<ResolvedRoute> routes, string? requestId)
    {
        var name = DisplayName(platform);
        var suffix = requestId is null ? "" : $" (CloudFront request {requestId})";
        var lead = $"{name}'s CDN is blocking this server, so the request never reached the API. " +
                   "It is not the API key: no key, valid or not, changes it.";

        if (routes.Count == 1)
            return lead + $" Route {name} traffic through a host that is not blocked by setting " +
                          $"Upstream:{platform} (see deploy/UPSTREAM-ROUTING.md), or get this server's " +
                          $"address unblocked.{suffix}";

        return lead + $" Refused on {string.Join(", ", routes.Select(r => r.Name))}, so the alternate hosts " +
                      $"are blocked too. See deploy/UPSTREAM-ROUTING.md.{suffix}";
    }

    private static bool IsDownloadUrl(string path) => path.EndsWith("/download-url", StringComparison.Ordinal);

    private static TimeSpan Clamp(TimeSpan delay) =>
        delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
        : delay > TimeSpan.FromMinutes(2) ? TimeSpan.FromMinutes(2)
        : delay;

    /// <summary>A 429 in the launcher's own JSON error shape, with a Retry-After the client honours.</summary>
    private ObjectResult RateLimited(string platform, TimeSpan retryAfter, string message, string code = "upstream_rate_limited")
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        Response.Headers["Retry-After"] = seconds.ToString();
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            error = message,
            code,
            platform = DisplayName(platform),
            retryAfterSeconds = seconds
        });
    }

    private ObjectResult Unreachable(string platform) =>
        StatusCode(StatusCodes.Status502BadGateway, new
        {
            error = $"{DisplayName(platform)} could not be reached from the launcher server. Try again in a moment.",
            code = "upstream_unreachable"
        });

    private static string DisplayName(string platform) => platform switch
    {
        "curseforge" => "CurseForge",
        "modrinth"   => "Modrinth",
        _            => "The store"
    };

    /// <summary>The POST body, or null when it is larger than <see cref="MaxBodyBytes"/>.</summary>
    private async Task<byte[]?> ReadBodyAsync(CancellationToken ct)
    {
        if (Request.ContentLength is > MaxBodyBytes) return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    /// <summary>Reads the rest of <paramref name="stream"/> after an already-read <paramref name="head"/>,
    /// stopping once past <paramref name="cap"/> bytes. <c>Complete</c> is false when the body was
    /// larger; the caller then streams the remainder instead of caching.</summary>
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

    /// <summary>Longest caller-supplied key accepted, so the header can't carry arbitrary data
    /// upstream. CurseForge keys are about 60 characters.</summary>
    private const int MaxOwnKeyLength = 200;

    /// <summary>The CurseForge key for this call and whether it is the caller's own. Modrinth calls
    /// get no credentials at all: its public API needs none, and nothing of the server's is ever
    /// attached to a proxied call except the CurseForge key on a CurseForge call.</summary>
    private async Task<(string? Key, bool OwnKey)> ResolveKeyAsync(string platform, CancellationToken ct)
    {
        if (platform != "curseforge") return (null, false);

        // A caller may bring their own CurseForge key (Settings > Mod stores). It is used for this
        // request only: never written to the database, never logged, never handed to another user.
        if (Request.Headers.TryGetValue(OwnKeyHeader, out var supplied)
            && supplied.ToString() is { Length: > 0 } own
            && own.Length <= MaxOwnKeyLength
            && own.All(c => c is > (char)0x20 and < (char)0x7F))
        {
            return (own, true);
        }

        // Read once a minute rather than once per proxied call (SettingsController drops the cache
        // when an admin changes a key).
        var key = await guard.GetCurseForgeKeyAsync(() =>
            db.GlobalSettings.AsNoTracking().Select(s => s.CurseForgeApiKey).FirstOrDefaultAsync(ct));
        return (key, false);
    }
}
