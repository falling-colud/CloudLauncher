using System.Net;
using System.Net.Http;

namespace CloudLauncher.Services;

/// <summary>Why a store call failed, after the retries <see cref="ApiClient.ProxyAsync"/> makes.</summary>
public enum StoreFailure
{
    /// <summary>The store or the launcher server asked for a pause that outlasted the retries.</summary>
    Busy,
    /// <summary>Nothing answered: the store is down, or the server could not reach it.</summary>
    Unreachable,
    /// <summary>The store's CDN is blocking the launcher server's address.</summary>
    Blocked,
    /// <summary>The store refused the launcher server's request (a 401 or 403 on the shared key).</summary>
    Refused,
    /// <summary>CurseForge rejected the key the user set in Settings > Mod stores.</summary>
    KeyRejected,
    /// <summary>The launcher server has no CurseForge key configured.</summary>
    KeyMissing,
    /// <summary>The project or file is no longer on the store.</summary>
    NotFound,
    /// <summary>A file whose author turned off third-party downloads.</summary>
    NotDistributable,
    Other
}

/// <summary>
/// A store (CurseForge or Modrinth) answered a call with an error. The message names the operation
/// and the reason, for the log; <see cref="Plain"/> is the one calm sentence a status line shows.
/// </summary>
/// <remarks>An <see cref="HttpRequestException"/> with its status code, so the places that already
/// tell "the store answered" from "nothing answered" by <c>StatusCode</c> keep working.</remarks>
public sealed class StoreRequestException : HttpRequestException
{
    public StoreRequestException(string store, string operation, StoreFailure failure, string plain,
        HttpStatusCode status, string? detail = null)
        : base($"{store} {operation} failed: {plain}" + (detail is null ? "" : $" ({detail})"), null, status)
    {
        Store = store;
        Operation = operation;
        Failure = failure;
        Plain = plain;
    }

    /// <summary>"CurseForge" or "Modrinth".</summary>
    public string Store { get; }

    /// <summary>What was being done: "search", "version list", "download URL".</summary>
    public string Operation { get; }

    public StoreFailure Failure { get; }

    /// <summary>One sentence for a status line, e.g. "CurseForge is busy right now. Try again in a
    /// minute."</summary>
    public string Plain { get; }

    /// <summary>The calm sentence when <paramref name="ex"/> is a store failure, else null, so a page
    /// can prefer it over its generic wording.</summary>
    public static string? PlainFor(Exception? ex) => ex is StoreRequestException store ? store.Plain : null;

    /// <summary>The standard sentence for a store that is pausing callers.</summary>
    public static string BusySentence(string store) => $"{store} is busy right now. Try again in a minute.";

    /// <summary>The standard sentence for a store that could not be reached.</summary>
    public static string UnreachableSentence(string store) => $"{store} could not be reached. Try again in a minute.";

    /// <summary>
    /// The launcher server's JSON error shape, <c>{"error": sentence, "code": name}</c>, read out of a
    /// body. Modrinth's own errors (<c>{"error": name, "description": sentence}</c>) come back as the
    /// description with no code; anything else as nothing.
    /// </summary>
    public static (string? Message, string? Code) ReadServerError(string? body)
    {
        var trimmed = body?.TrimStart();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '{') return (null, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, null);

            var code = Text(root, "code");
            if (code is not null)
            {
                var message = Text(root, "error");
                if (Text(root, "detail") is { } detail) message = message is null ? detail : $"{message} ({detail})";
                return (message, code);
            }
            return (Text(root, "description") ?? Text(root, "error"), null);
        }
        catch (System.Text.Json.JsonException) { return (null, null); }

        static string? Text(System.Text.Json.JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
            && p.GetString() is { } s && !string.IsNullOrWhiteSpace(s) ? s : null;
    }

    /// <summary>A one-line gist of an error body for the log: tags stripped, whitespace folded, cut
    /// short. Null for an empty body.</summary>
    public static string? Gist(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var text = System.Text.RegularExpressions.Regex.Replace(body, "<[^>]*>", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0) return null;
        return text.Length > 180 ? text[..180].TrimEnd() + "..." : text;
    }
}
