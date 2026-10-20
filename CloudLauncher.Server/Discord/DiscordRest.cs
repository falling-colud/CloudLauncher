using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudLauncher.Server.Discord;

/// <summary>A file sent with a message, referenced from its components as <c>attachment://Name</c>.</summary>
public sealed record DiscordFile(string Name, byte[] Data, string ContentType = "image/png");

/// <summary>An error answer from Discord's API.</summary>
/// <param name="Code">Discord's own error code from the JSON body, when it sent one (10003 unknown
/// channel, 10008 unknown message, 10015 unknown webhook, 50001 missing access, 50013 missing
/// permissions).</param>
public sealed class DiscordApiException(int status, int? code, string message, TimeSpan? retryAfter = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public int? Code { get; } = code;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>The message is gone (deleted by hand): post a new one.</summary>
    public bool MessageGone => Code == 10008 || (Status == 404 && Code is null);

    /// <summary>The channel or webhook is gone, or the app may no longer post there: retrying won't help.</summary>
    public bool TargetGone => Code is 10003 or 10015 or 50001 or 50013 || Status is 401 or 403;
}

/// <summary>
/// The few Discord REST calls the planning boards need: posting, editing and deleting messages through
/// a webhook or as the app's bot, plus what <c>/plan link</c> and the Activity look up.
/// </summary>
/// <remarks>
/// A 429 is waited out and retried when Discord asks for a short pause; a longer one surfaces as a
/// <see cref="DiscordApiException"/> with <see cref="DiscordApiException.RetryAfter"/> so the caller
/// can come back later instead of holding a thread.
/// </remarks>
public sealed class DiscordRest(IHttpClientFactory httpFactory, DiscordOptions options, ILogger<DiscordRest> log)
{
    public const string HttpClientName = "discord";

    /// <summary>The form Discord asks bots to name themselves in.</summary>
    public const string UserAgent = "DiscordBot (https://cloudlauncher.co, 1.0)";

    private static readonly TimeSpan MaxInlineWait = TimeSpan.FromSeconds(10);

    // ── webhooks ────────────────────────────────────────────────────────────────

    /// <summary>The webhook behind a pasted URL: proves it exists and says which channel it posts to.</summary>
    public Task<JsonElement> GetWebhookAsync(string id, string token, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"webhooks/{id}/{token}", null, null, bot: false, ct);

    /// <summary>Posts a message through a webhook and answers its id.</summary>
    public async Task<string> ExecuteWebhookAsync(string id, string token, JsonObject payload,
        IReadOnlyList<DiscordFile> files, CancellationToken ct)
    {
        var message = await SendAsync(HttpMethod.Post, $"webhooks/{id}/{token}?wait=true&with_components=true",
            payload, files, bot: false, ct);
        return message.GetProperty("id").GetString()!;
    }

    public Task EditWebhookMessageAsync(string id, string token, string messageId, JsonObject payload,
        IReadOnlyList<DiscordFile> files, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"webhooks/{id}/{token}/messages/{messageId}?with_components=true",
            payload, files, bot: false, ct);

    public Task DeleteWebhookMessageAsync(string id, string token, string messageId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"webhooks/{id}/{token}/messages/{messageId}", null, null, bot: false, ct);

    // ── as the app's bot ────────────────────────────────────────────────────────

    public async Task<string> CreateMessageAsync(string channelId, JsonObject payload,
        IReadOnlyList<DiscordFile> files, CancellationToken ct)
    {
        var message = await SendAsync(HttpMethod.Post, $"channels/{channelId}/messages", payload, files, bot: true, ct);
        return message.GetProperty("id").GetString()!;
    }

    public Task EditMessageAsync(string channelId, string messageId, JsonObject payload,
        IReadOnlyList<DiscordFile> files, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"channels/{channelId}/messages/{messageId}", payload, files, bot: true, ct);

    public Task DeleteMessageAsync(string channelId, string messageId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"channels/{channelId}/messages/{messageId}", null, null, bot: true, ct);

    public Task<JsonElement> GetChannelAsync(string channelId, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"channels/{channelId}", null, null, bot: true, ct);

    /// <summary>A running Activity instance: where it was opened and who is in it, or null when Discord
    /// doesn't know the id (made up, or the Activity has closed).</summary>
    public async Task<JsonElement?> GetActivityInstanceAsync(string instanceId, CancellationToken ct)
    {
        try
        {
            return await SendAsync(HttpMethod.Get,
                $"applications/{options.ApplicationId}/activity-instances/{Uri.EscapeDataString(instanceId)}",
                null, null, bot: true, ct);
        }
        catch (DiscordApiException e) when (e.Status == 404)
        {
            return null;
        }
    }

    /// <summary>Creates a global slash command, or replaces the one with the same name.</summary>
    public Task<JsonElement> UpsertGlobalCommandAsync(JsonObject command, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"applications/{options.ApplicationId}/commands", command, null, bot: true, ct);

    // ── plumbing ────────────────────────────────────────────────────────────────

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonObject? payload,
        IReadOnlyList<DiscordFile>? files, bool bot, CancellationToken ct)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, $"{options.ApiBase.TrimEnd('/')}/{path}");
            request.Headers.UserAgent.ParseAdd(UserAgent);
            if (bot)
            {
                if (string.IsNullOrWhiteSpace(options.BotToken))
                    throw new InvalidOperationException("Discord:BotToken is not set");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bot", options.BotToken);
            }
            if (payload is not null) request.Content = Body(payload, files);

            using var response = await http.SendAsync(request, ct);
            var text = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(text))
                    return default;
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.Clone();
            }

            var (code, message, retry) = ReadError(response, text);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4
                && retry is { } wait && wait <= MaxInlineWait)
            {
                log.LogInformation("Discord asked to wait {Wait} before {Method} {Path}", wait, method, Redact(path));
                await Task.Delay(wait + TimeSpan.FromMilliseconds(100), ct);
                continue;
            }
            throw new DiscordApiException((int)response.StatusCode, code,
                $"Discord {(int)response.StatusCode} on {method} {Redact(path)}: {message}", retry);
        }
    }

    private static HttpContent Body(JsonObject payload, IReadOnlyList<DiscordFile>? files)
    {
        var json = payload.ToJsonString();
        if (files is not { Count: > 0 })
            return new StringContent(json, Encoding.UTF8, "application/json");

        var multipart = new MultipartFormDataContent();
        var payloadPart = new StringContent(json, Encoding.UTF8, "application/json");
        multipart.Add(payloadPart, "payload_json");
        for (var i = 0; i < files.Count; i++)
        {
            var part = new ByteArrayContent(files[i].Data);
            part.Headers.ContentType = new MediaTypeHeaderValue(files[i].ContentType);
            multipart.Add(part, $"files[{i}]", files[i].Name);
        }
        return multipart;
    }

    private static (int? Code, string Message, TimeSpan? RetryAfter) ReadError(HttpResponseMessage response, string text)
    {
        int? code = null;
        var message = text.Length > 300 ? text[..300] : text;
        TimeSpan? retry = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number) code = c.GetInt32();
            if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString()!;
            if (root.TryGetProperty("errors", out var errors)) message += " " + Trim(errors.GetRawText(), 400);
            if (root.TryGetProperty("retry_after", out var r) && r.ValueKind == JsonValueKind.Number)
                retry = TimeSpan.FromSeconds(r.GetDouble());
        }
        catch (JsonException) { /* not JSON: keep the raw text */ }

        if (retry is null && response.Headers.RetryAfter?.Delta is { } delta) retry = delta;
        return (code, message, retry);
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] + "..." : s;

    /// <summary>A webhook's token is a password: keep it out of logs and error messages.</summary>
    private static string Redact(string path)
    {
        if (!path.StartsWith("webhooks/", StringComparison.Ordinal)) return path;
        var parts = path.Split('/');
        if (parts.Length > 2) parts[2] = parts[2].Split('?')[0] is { Length: > 0 } ? "***" : parts[2];
        return string.Join('/', parts);
    }
}
