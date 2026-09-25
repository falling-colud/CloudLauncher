using System.Net;
using CloudLauncher.Server.Auth;
using Microsoft.AspNetCore.HttpOverrides;

namespace CloudLauncher.Server.Net;

/// <summary>Limits what the server answers over plain HTTP to the launcher's update feed and the
/// public website.</summary>
/// <remarks>
/// <para>A request is plain HTTP when it reached Kestrel directly from the internet (port 5000, which
/// only very old launchers use to update themselves), or when nginx proxied it from its plain-HTTP
/// port and said so in <c>X-Forwarded-Proto</c>. Loopback requests without that header (local
/// tools, health checks) are let through.</para>
/// <para>Must run before <c>UseForwardedHeaders</c>, which overwrites the peer address and the
/// header.</para>
/// <para><c>App:DirectHttp</c> = <c>Full</c> turns the gate off.</para>
/// </remarks>
public sealed class DirectHttpGate(RequestDelegate next, AppOptions options)
{
    private static readonly string[] AllowedPaths =
    {
        "/launcher/latest", "/launcher/releases", "/launcher/download", "/download", "/health",
        "/", "/index.html", "/site.css", "/site.js", "/favicon.ico", "/robots.txt",
        "/privacy", "/terms", "/privacy.html", "/terms.html",
    };

    private static readonly string[] AllowedPrefixes = { "/fonts", "/img", "/.well-known" };

    private readonly bool _off = string.Equals(options.DirectHttp, AppOptions.DirectHttpFull, StringComparison.OrdinalIgnoreCase);

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (_off || IsExempt(request.Path) || !ArrivedOverPlainHttp(context) || IsAllowed(request))
            return next(context);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return context.Response.WriteAsJsonAsync(new
        {
            error = "This address only serves launcher updates. Update CloudLauncher to keep using it."
        });
    }

    /// <summary>Certificate challenges and the security contact must answer on any address.</summary>
    private static bool IsExempt(PathString path) =>
        path.StartsWithSegments("/.well-known/acme-challenge", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/.well-known/security.txt", StringComparison.OrdinalIgnoreCase);

    private static bool ArrivedOverPlainHttp(HttpContext context)
    {
        if (!IsLoopback(context.Connection.RemoteIpAddress)) return true;

        if (!context.Request.Headers.TryGetValue(ForwardedHeadersDefaults.XForwardedProtoHeaderName, out var proto))
            return false;
        // nginx sets this header itself, so the last value is the one it wrote.
        var last = proto.ToString().Split(',').Last().Trim();
        return !string.Equals(last, "https", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopback(IPAddress? address)
    {
        // No address means a non-TCP transport, which only something on this machine can use.
        if (address is null) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    private static bool IsAllowed(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;

        var path = request.Path;
        if (!path.HasValue) return true;
        foreach (var allowed in AllowedPaths)
            if (path.Equals(allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        foreach (var prefix in AllowedPrefixes)
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
