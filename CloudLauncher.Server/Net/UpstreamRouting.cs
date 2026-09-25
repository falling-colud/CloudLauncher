using System.Collections.Concurrent;
using System.Net;
using System.Reflection;

namespace CloudLauncher.Server.Net;

/// <summary>
/// One way to reach an upstream mod-platform API: a relay that forwards to the real API
/// (<see cref="BaseUrl"/>), an outbound proxy (<see cref="HttpProxy"/>), or both. Configured under
/// "Upstream:{platform}"; see deploy/UPSTREAM-ROUTING.md.
/// </summary>
public sealed class UpstreamRoute
{
    /// <summary>Label used in logs and admin-facing errors. Defaults to the hosts involved.</summary>
    public string? Name { get; set; }

    /// <summary>Relay base URL replacing the platform's own, e.g. "https://cf-relay.example.net/v1".
    /// A relay terminates TLS and sees the API key, so only point this at a host you control.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Outbound HTTP proxy, e.g. "http://10.0.0.5:3128". TLS stays end-to-end through
    /// CONNECT, so the proxy never sees the API key; prefer this over a relay. Credentials may be
    /// embedded ("http://user:pass@host:3128") or given separately below.</summary>
    public string? HttpProxy { get; set; }

    public string? ProxyUser { get; set; }
    public string? ProxyPassword { get; set; }

    /// <summary>Extra request headers, for a relay that wants its own auth token.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Bound from the "Upstream" configuration section. Empty by default, which means one
/// direct route per platform.</summary>
public sealed class UpstreamRoutingOptions
{
    /// <summary>Try the platform's own API directly first. Leave on unless this server's address is
    /// permanently blocked: a direct route is faster and keeps the API key off any relay. When off, a
    /// platform with no alternate route still keeps its direct route.</summary>
    public bool UseDirect { get; set; } = true;

    /// <summary>How long a blocked route stays out of rotation before it is tried again. A dead route
    /// doesn't cost every request a round trip, and an unblock is picked up within one window.</summary>
    public int BlockCooldownMinutes { get; set; } = 10;

    public List<UpstreamRoute> CurseForge { get; set; } = new();
    public List<UpstreamRoute> Modrinth { get; set; } = new();

    public IReadOnlyList<UpstreamRoute> For(string platform) => platform.ToLowerInvariant() switch
    {
        "curseforge" => CurseForge,
        "modrinth"   => Modrinth,
        _            => Array.Empty<UpstreamRoute>()
    };
}

/// <summary>A validated, ready-to-use route with its defaults filled in.</summary>
public sealed class ResolvedRoute
{
    public required string Platform { get; init; }
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required string BaseUrl { get; init; }
    public string? HttpProxy { get; init; }
    public string? ProxyUser { get; init; }
    public string? ProxyPassword { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True for the platform's own API reached with no proxy in between.</summary>
    public required bool IsDirect { get; init; }

    /// <summary>Named <see cref="HttpClient"/> backing this route. Each route needs its own client
    /// because the outbound proxy lives on the handler, which a client cannot change per request.</summary>
    public string ClientName => $"upstream:{Platform}:{Index}";

    public override string ToString() => Name;
}

/// <summary>The full set of routes per platform, built once at startup so registration and
/// request handling agree on client names.</summary>
public sealed class UpstreamRoutingPlan
{
    private static readonly Dictionary<string, string> DirectBaseUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["curseforge"] = "https://api.curseforge.com/v1",
        ["modrinth"]   = "https://api.modrinth.com/v2"
    };

    private readonly Dictionary<string, IReadOnlyList<ResolvedRoute>> _byPlatform;

    public IReadOnlyList<ResolvedRoute> All { get; }
    public TimeSpan BlockCooldown { get; }

    private UpstreamRoutingPlan(Dictionary<string, IReadOnlyList<ResolvedRoute>> byPlatform, TimeSpan cooldown)
    {
        _byPlatform = byPlatform;
        BlockCooldown = cooldown;
        All = byPlatform.Values.SelectMany(r => r).ToList();
    }

    /// <summary>Routes for a platform in configured order, or empty for an unknown platform.</summary>
    public IReadOnlyList<ResolvedRoute> For(string platform) =>
        _byPlatform.TryGetValue(platform, out var routes) ? routes : Array.Empty<ResolvedRoute>();

    public static UpstreamRoutingPlan Build(UpstreamRoutingOptions options)
    {
        var byPlatform = new Dictionary<string, IReadOnlyList<ResolvedRoute>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (platform, directBaseUrl) in DirectBaseUrls)
        {
            var configured = options.For(platform);
            var routes = new List<ResolvedRoute>();

            // Dropping the direct route only makes sense where an alternate exists; otherwise the
            // platform would have nowhere to go at all.
            if (options.UseDirect || configured.Count == 0)
                routes.Add(new ResolvedRoute
                {
                    Platform = platform,
                    Index = 0,
                    Name = "direct",
                    BaseUrl = directBaseUrl,
                    IsDirect = true
                });

            foreach (var route in configured)
                routes.Add(Resolve(platform, routes.Count, route, directBaseUrl));

            byPlatform[platform] = routes;
        }

        var minutes = options.BlockCooldownMinutes > 0 ? options.BlockCooldownMinutes : 10;
        return new UpstreamRoutingPlan(byPlatform, TimeSpan.FromMinutes(minutes));
    }

    private static ResolvedRoute Resolve(string platform, int index, UpstreamRoute route, string directBaseUrl)
    {
        var where = $"Upstream:{platform}:{index}";

        if (string.IsNullOrWhiteSpace(route.BaseUrl) && string.IsNullOrWhiteSpace(route.HttpProxy))
            throw new InvalidOperationException(
                $"{where} sets neither BaseUrl nor HttpProxy, so it is not a route to anywhere. " +
                "Give it a relay BaseUrl, an outbound HttpProxy, or remove it.");

        var baseUrl = string.IsNullOrWhiteSpace(route.BaseUrl)
            ? directBaseUrl
            : ParseHttpUri($"{where}:BaseUrl", route.BaseUrl).ToString().TrimEnd('/');

        string? proxyAddress = null;
        var proxyUser = route.ProxyUser;
        var proxyPassword = route.ProxyPassword;

        if (!string.IsNullOrWhiteSpace(route.HttpProxy))
        {
            var proxyUri = ParseHttpUri($"{where}:HttpProxy", route.HttpProxy);
            // Proxy vendors hand out URLs with embedded credentials ("http://user:pass@host:3128"), but
            // WebProxy ignores that part, so pull it out here.
            if (!string.IsNullOrEmpty(proxyUri.UserInfo))
            {
                var parts = proxyUri.UserInfo.Split(':', 2);
                proxyUser ??= Uri.UnescapeDataString(parts[0]);
                proxyPassword ??= parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
                proxyAddress = new UriBuilder(proxyUri) { UserName = "", Password = "" }.Uri.ToString();
            }
            else
            {
                proxyAddress = proxyUri.ToString();
            }
        }

        return new ResolvedRoute
        {
            Platform = platform,
            Index = index,
            Name = route.Name is { Length: > 0 } n ? n : DescribeRoute(route.BaseUrl, proxyAddress),
            BaseUrl = baseUrl,
            HttpProxy = proxyAddress,
            ProxyUser = proxyUser,
            ProxyPassword = proxyPassword,
            Headers = new Dictionary<string, string>(route.Headers, StringComparer.OrdinalIgnoreCase),
            IsDirect = false
        };
    }

    private static string DescribeRoute(string? baseUrl, string? proxyAddress)
    {
        var relayHost = baseUrl is { Length: > 0 } b ? new Uri(b).Host : null;
        var proxyHost = proxyAddress is { Length: > 0 } p ? new Uri(p).Host : null;
        return (relayHost, proxyHost) switch
        {
            (not null, not null) => $"{relayHost} via {proxyHost}",
            (not null, null)     => relayHost,
            (null, not null)     => $"direct via {proxyHost}",
            _                    => "unnamed"
        };
    }

    private static Uri ParseHttpUri(string where, string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"{where} must be an absolute http(s) URL, but is '{value}'.");
        return uri;
    }
}

/// <summary>
/// Picks the route for an upstream call and tracks which routes are being refused.
/// </summary>
/// <remarks>
/// Used for failover: when CurseForge's CDN blocks this server's address, the call is retried
/// through a configured relay or proxy. A blocked route is parked for a cooldown and then tried
/// again, so an unblock needs no redeploy.
/// </remarks>
public sealed class UpstreamRouter(
    UpstreamRoutingPlan plan,
    IHttpClientFactory factory,
    ILogger<UpstreamRouter> log)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedUntil = new();

    /// <summary>Routes to try, best first: healthy ones in configured order, then any in cooldown as
    /// a last resort, so a dead relay can still fall back to a route that is only suspect.</summary>
    public IReadOnlyList<ResolvedRoute> RoutesFor(string platform)
    {
        var all = plan.For(platform);
        if (all.Count < 2) return all;

        var now = DateTimeOffset.UtcNow;
        var healthy = new List<ResolvedRoute>(all.Count);
        var cooling = new List<ResolvedRoute>();
        foreach (var route in all)
            (IsCooling(route, now) ? cooling : healthy).Add(route);

        if (cooling.Count == 0) return all;
        healthy.AddRange(cooling);
        return healthy;
    }

    public HttpClient ClientFor(ResolvedRoute route) => factory.CreateClient(route.ClientName);

    public void MarkBlocked(ResolvedRoute route, string reason)
    {
        var until = DateTimeOffset.UtcNow + plan.BlockCooldown;
        _blockedUntil[route.ClientName] = until;
        log.LogWarning(
            "Upstream route '{Route}' for {Platform} is not usable ({Reason}). Skipping it until {Until:u}.",
            route.Name, route.Platform, reason, until);
    }

    /// <summary>Called when a route returns a response from the API itself. A 404 or a rejected key
    /// counts too, since the request got through.</summary>
    public void MarkHealthy(ResolvedRoute route)
    {
        if (_blockedUntil.TryRemove(route.ClientName, out _))
            log.LogInformation(
                "Upstream route '{Route}' for {Platform} is reachable again.", route.Name, route.Platform);
    }

    private bool IsCooling(ResolvedRoute route, DateTimeOffset now) =>
        _blockedUntil.TryGetValue(route.ClientName, out var until) && until > now;
}

public static class UpstreamRoutingServiceCollectionExtensions
{
    /// <summary>Binds "Upstream", validates it, and registers one named HttpClient per route. Bad
    /// config throws here at startup instead of failing later when someone browses mods.</summary>
    public static IServiceCollection AddUpstreamRouting(this IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection("Upstream").Get<UpstreamRoutingOptions>() ?? new UpstreamRoutingOptions();
        var plan = UpstreamRoutingPlan.Build(options);

        services.AddSingleton(plan);
        services.AddSingleton<UpstreamRouter>();

        foreach (var route in plan.All)
        {
            var builder = services.AddHttpClient(route.ClientName, c =>
            {
                c.Timeout = TimeSpan.FromMinutes(2);
                c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            });

            // Redirects are not followed, so the API key is never sent wherever a store response points;
            // the client gets the redirect as is. The proxy lives on the handler, hence one named client per
            // route. IHttpClientFactory manages handler lifetime so DNS changes are still picked up.
            builder.ConfigurePrimaryHttpMessageHandler(() => route.HttpProxy is null
                ? new SocketsHttpHandler { AllowAutoRedirect = false }
                : new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    Proxy = BuildWebProxy(route),
                    UseProxy = true
                });
        }

        return services;
    }

    /// <summary>
    /// User-Agent for every store call from this server: project, build and contact, in the form
    /// Modrinth asks API clients to use.
    /// </summary>
    public static string UserAgent { get; } = BuildUserAgent();

    private static string BuildUserAgent()
    {
        var assembly = typeof(UpstreamRouter).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString(3)
                      ?? "0.0.0";
        var plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        return $"falling-colud/CloudLauncher-Server/{version} ({CloudLauncher.Shared.Legal.ContactEmail})";
    }

    private static WebProxy BuildWebProxy(ResolvedRoute route)
    {
        var proxy = new WebProxy(route.HttpProxy!) { BypassProxyOnLocal = false };
        if (!string.IsNullOrEmpty(route.ProxyUser))
            proxy.Credentials = new NetworkCredential(route.ProxyUser, route.ProxyPassword ?? "");
        return proxy;
    }
}
