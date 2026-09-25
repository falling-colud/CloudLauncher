using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CloudLauncher.Shared;

namespace CloudLauncher.Services;

/// <summary>The account routes: renaming, exporting and deleting the signed-in account, and the
/// server's legal pages.</summary>
public sealed partial class ApiClient
{
    /// <summary>A page on the launcher's server, such as <see cref="Legal.TermsPath"/>.</summary>
    public string LegalPageUrl(string path) => ServerUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    /// <summary>Reads <c>auth/me</c> and keeps what this PC remembers about the account (its name and
    /// whether it is an administrator) in step with it.</summary>
    public async Task<UserSummary> RefreshAccountAsync(CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        RememberAccount(me);
        return me;
    }

    /// <summary>Renames the signed-in account.</summary>
    /// <exception cref="ApiException">409 when the name is taken, 400 with the reason when it is not
    /// allowed.</exception>
    public async Task<UserSummary> ChangeUsernameAsync(string username, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var me = await ReadAsync<UserSummary>(
            await _http.PatchAsJsonAsync("auth/me/username", new ChangeUsernameRequest(username), JsonOpts, ct), ct);
        RememberAccount(me);
        return me;
    }

    /// <summary>Saves the server's export of everything it holds about the account to
    /// <paramref name="destination"/>.</summary>
    /// <remarks>Written beside the destination first and moved into place at the end, so a failed
    /// download never leaves a half-written file under the name the user chose.</remarks>
    public async Task ExportMyDataAsync(string destination, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        using var resp = await _http.GetAsync("auth/me/export", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessKeepBody(resp, ct);
        // A captive portal answers 200 with a web page; that is not the user's data.
        if (resp.Content.Headers.ContentType?.MediaType is { } type
            && type.Contains("html", StringComparison.OrdinalIgnoreCase))
            throw new ApiException("The server did not send a data export.", resp.StatusCode);

        var partial = destination + ".part";
        try
        {
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, useAsync: true))
                await resp.Content.CopyToAsync(file, ct);
            File.Move(partial, destination, overwrite: true);
        }
        catch
        {
            try { File.Delete(partial); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Deletes the signed-in account on the server, then forgets it here the way signing out
    /// does.</summary>
    /// <param name="req">The password (null for an account without one) and the typed "DELETE".</param>
    public async Task DeleteAccountAsync(DeleteAccountRequest req, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "auth/me")
        {
            Content = JsonContent.Create(req, options: JsonOpts)
        };
        await EnsureSuccess(await _http.SendAsync(request, ct), ct);

        // The account no longer exists, so its tokens and everything cached for it are worthless. The
        // deletion has happened either way, so a settings file that cannot be saved is only logged.
        _settings.AdminAccountId = null;
        _settings.PasswordAccountId = null;
        try { ClearTokens(); }
        catch (Exception ex)
        {
            AppLog.LogError("account", ex);
            _http.DefaultRequestHeaders.Authorization = null;
            ForgetAccountCaches();
        }
    }

    /// <summary>
    /// The sentence the server put in a refusal (<c>{"error": "..."}</c>), or null when it sent none.
    /// </summary>
    /// <remarks>An <see cref="ApiException"/> message reads <c>"409 Conflict: {body}"</c>.</remarks>
    public static string? ServerSentence(ApiException ex)
    {
        var message = ex.Message;
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) return null;
        var body = message[(colon + 2)..].Trim();
        if (body.Length == 0 || body.StartsWith('<')) return null;
        if (!body.StartsWith('{')) return body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var name in new[] { "error", "detail", "title", "message" })
                if (doc.RootElement.TryGetProperty(name, out var el)
                    && el.ValueKind == JsonValueKind.String
                    && el.GetString() is { Length: > 0 } sentence)
                    return sentence;
        }
        catch (JsonException) { /* not JSON after all */ }
        return null;
    }

    /// <summary>Stores what <c>auth/me</c> said about the signed-in account.</summary>
    private void RememberAccount(UserSummary me)
    {
        // Somebody else signed in while the request was out: this answer is not about them.
        if (_settings.UserId != me.Id) return;

        var changed = false;
        var admin = me.IsAdmin ? me.Id : (Guid?)null;
        if (_settings.AdminAccountId != admin)
        {
            _settings.AdminAccountId = admin;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(me.Username) && !string.Equals(_settings.Username, me.Username, StringComparison.Ordinal))
        {
            _settings.Username = me.Username;
            changed = true;
        }
        if (!changed) return;
        try { _settings.Save(); }
        catch (Exception ex) { AppLog.LogError("account", ex); }
    }
}
