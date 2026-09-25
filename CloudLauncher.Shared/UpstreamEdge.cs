using System.Text.RegularExpressions;

namespace CloudLauncher.Shared;

/// <summary>
/// Tells a CDN edge block apart from a real upstream API error.
///
/// api.curseforge.com sits behind CloudFront, whose WAF answers blocked callers (datacenter IPs, for
/// example) with an HTML "Request blocked" page before the API sees the request. No API key gets past
/// that, so the server reroutes around a block and the client explains it.
/// </summary>
public static class UpstreamEdge
{
    private static readonly Regex RequestIdPattern =
        new(@"Request ID:\s*([A-Za-z0-9_=\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True when a response body is an edge block page rather than an API error payload.
    /// These APIs answer in JSON, so a JSON body is never a block, even if it mentions "cloudfront".</summary>
    public static bool IsBlockPage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        var head = body.AsSpan().TrimStart();
        if (head[0] is '{' or '[') return false;
        return body.Contains("cloudfront", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The CloudFront request id from a block page, if it has one. CurseForge support needs it
    /// to unblock an address.</summary>
    public static string? RequestId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var match = RequestIdPattern.Match(body);
        return match.Success ? match.Groups[1].Value : null;
    }
}
