using System.Net;
using System.Net.Http;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

public sealed partial class ApiClient
{
    /// <summary>The User-Agent sent to our own server and to the mod stores.</summary>
    /// <remarks>The server tells launcher builds apart by the leading
    /// <c>CloudLauncher/{version}</c> (see <see cref="AppVersion.Rank"/>), so that part always
    /// comes first. The rest is the contact info the stores ask API clients to include.</remarks>
    public static string UserAgent { get; } =
        $"CloudLauncher/{AppVersion.CurrentString} (+https://cloudlauncher.co; {Legal.ContactEmail})";

    /// <summary>Makes <paramref name="client"/> send <see cref="UserAgent"/>.</summary>
    public static void ApplyUserAgent(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    /// <summary><see cref="ApplyUserAgent"/> for field initialisers: returns the same client.</summary>
    public static HttpClient WithUserAgent(HttpClient client)
    {
        ApplyUserAgent(client);
        return client;
    }

    /// <summary>The error any call gets that would put a password or token on plain http to another
    /// machine. 426 is HTTP's own "switch to a secure protocol".</summary>
    private ApiException InsecureTransport() => new(
        $"CloudLauncher does not send passwords or sign-in tokens over plain http. Use the server's https:// address instead of {ServerUrl}.",
        HttpStatusCode.UpgradeRequired);

    /// <summary>Refuses any request that would send a password or token over plain http to another
    /// machine.</summary>
    /// <remarks>Backstop for the checks in the calls themselves: it sees every outgoing request,
    /// default headers included, so a newly added route can't skip it. Anything under <c>/auth/</c>
    /// counts, since those routes take a password or return tokens.</remarks>
    private sealed class TransportGuard(ApiClient owner, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri is { IsAbsoluteUri: true } uri && !AppSettings.IsSecureServerUri(uri)
                && CarriesCredentials(request))
                return Task.FromException<HttpResponseMessage>(owner.InsecureTransport());
            return base.SendAsync(request, ct);
        }

        private static bool CarriesCredentials(HttpRequestMessage request) =>
            request.Headers.Authorization is not null
            || request.Headers.Contains(OwnCurseForgeKeyHeader)
            || request.RequestUri!.AbsolutePath.Contains("/auth/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where the direct CurseForge route sends calls. Test harnesses can override it with
    /// <c>CL_CURSEFORGE_BASE</c>, but only with a local address, because the route carries the
    /// user's own API key.</summary>
    private static string ResolveDirectCurseForgeBase()
    {
        const string real = "https://api.curseforge.com/v1";
        var configured = Environment.GetEnvironmentVariable("CL_CURSEFORGE_BASE")?.Trim();
        if (string.IsNullOrEmpty(configured)) return real;
        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && AppSettings.IsLoopbackHost(uri))
            return configured.TrimEnd('/');
        AppLog.Log("curseforge", "Ignoring CL_CURSEFORGE_BASE: it may only point at 127.0.0.1 or localhost.");
        return real;
    }
}
