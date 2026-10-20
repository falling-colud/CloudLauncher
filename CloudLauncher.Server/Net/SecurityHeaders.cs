using Microsoft.Net.Http.Headers;

namespace CloudLauncher.Server.Net;

/// <summary>Adds the browser security headers to every response.</summary>
/// <remarks>Written when the response starts, so this can sit first in the pipeline and still cover
/// every refusal, and <see cref="HttpRequest.IsHttps"/> already reflects nginx's
/// <c>X-Forwarded-Proto</c> by then. Headers a handler set itself are left alone.</remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>For HTML only. Inline styles are allowed because the small pages the auth routes
    /// render carry their own; scripts must come from this host.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
        "font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; " +
        "frame-ancestors 'none'";

    /// <summary>Where the Discord Activity is served from (Discord's URL mapping points here).</summary>
    public const string ActivityPath = "/discord-activity";

    /// <summary>The Discord Activity (<see cref="ActivityPath"/>) is the same kind of page, but it runs
    /// inside Discord's own window, through Discord's proxy on discordsays.com, so Discord may frame it.</summary>
    public const string ActivityContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; " +
        "font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; " +
        "frame-ancestors https://discord.com https://*.discord.com https://*.discordsays.com";

    /// <summary>Set on a request whose path was a <see cref="ActivityPath"/> alias before it was rewritten.</summary>
    public const string ActivityItem = "cl.activity";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var http = (HttpContext)state;
            var headers = http.Response.Headers;
            var activity = http.Items.ContainsKey(ActivityItem) || http.Request.Path.StartsWithSegments(ActivityPath);
            SetIfMissing(headers, HeaderNames.XContentTypeOptions, "nosniff");
            SetIfMissing(headers, "Referrer-Policy", "no-referrer");
            if (!activity) SetIfMissing(headers, HeaderNames.XFrameOptions, "DENY");
            if (http.Request.IsHttps)
                SetIfMissing(headers, HeaderNames.StrictTransportSecurity, "max-age=31536000");
            if (IsHtml(http.Response.ContentType))
                SetIfMissing(headers, HeaderNames.ContentSecurityPolicy,
                    activity ? ActivityContentSecurityPolicy : ContentSecurityPolicy);
            return Task.CompletedTask;
        }, context);

        return next(context);
    }

    private static void SetIfMissing(IHeaderDictionary headers, string name, string value)
    {
        if (!headers.ContainsKey(name)) headers[name] = value;
    }

    private static bool IsHtml(string? contentType) =>
        contentType is not null && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
}
