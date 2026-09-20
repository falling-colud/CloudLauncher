using System.Text.RegularExpressions;

namespace CloudLauncher.Shared;

/// <summary>
/// Tells a CDN edge block apart from a real upstream API error.
///
/// api.curseforge.com sits behind CloudFront, whose WAF answers blocked callers with an HTML
/// "Request blocked" page before the API ever sees the request. Datacenter egress trips that, so a
/// server can be refused while every desktop client reaches the same API fine — and no API key,
/// valid or not, changes it. The two faults have completely different fixes, so both the server
/// (which reroutes around a block) and the client (which explains one) need to recognise it.
/// </summary>
public static class UpstreamEdge
{
    private static readonly Regex RequestIdPattern =
        new(@"Request ID:\s*([A-Za-z0-9_=\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True when a response body is an edge block page rather than an API error payload.
    /// Every API here answers in JSON, so an HTML body on an error status did not come from the API.</summary>
    public static bool IsBlockPage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var head = body.AsSpan().TrimStart();
        return body.Contains("cloudfront", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The CloudFront request id from a block page, if it carries one. Worth keeping: it is
    /// the one part of the page CurseForge support can act on when asked to unblock an address.</summary>
    public static string? RequestId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var match = RequestIdPattern.Match(body);
        return match.Success ? match.Groups[1].Value : null;
    }
}
