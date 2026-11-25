using System.Net.Http;
using System.Text.Json;
using CmlLib.Core.Auth.Microsoft;
using CloudLauncher.Services;
using XboxAuthNet.Game;
using XboxAuthNet.Game.Authenticators;
using XboxAuthNet.Game.OAuth;
using XboxAuthNet.OAuth;

namespace CloudLauncher;

/// <summary>What the user needs to finish a Microsoft sign-in on another screen.</summary>
public sealed record DeviceCodePrompt(string UserCode, string VerificationUri, DateTimeOffset ExpiresAt);

/// <summary>
/// Microsoft sign-in without an embedded browser, for the Linux launcher: the device-code flow. The
/// launcher shows a short code, the user enters it at microsoft.com/link in any browser (this computer
/// or a phone) and signs in there, and the launcher picks the tokens up. Same Microsoft application and
/// token format as the Windows launcher's browser window, so everything after (Xbox, Minecraft, silent
/// refresh) is CmlLib's own.
/// </summary>
public static class DeviceCodeSignIn
{
    private const string DeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CloudLauncher/{AppVersion.CurrentString}");
        return http;
    }

    /// <summary>Shows the code (set by the window). Called on any thread when a sign-in needs the user;
    /// cancelling the token source stops the sign-in. Returns once the prompt is up.</summary>
    public static Func<DeviceCodePrompt, CancellationTokenSource, Task>? CodeReady { get; set; }

    /// <summary>Raised (any thread) when a sign-in that showed a code has finished, either way, so the
    /// prompt can close.</summary>
    public static event Action? Finished;

    /// <summary>The sign-in CmlLib's login handler uses on Linux: silent refresh first, the device code
    /// when that is not enough.</summary>
    public static IAuthenticationProvider CreateProvider() =>
        new Provider(JELoginHandler.DefaultMicrosoftOAuthClientInfo);

    private sealed class Provider(MicrosoftOAuthClientInfo client) : IAuthenticationProvider
    {
        private readonly MicrosoftOAuthBuilder _oauth = new(client);

        private DeviceCodeOAuth Interactive() =>
            new(new MicrosoftOAuthParameters(client, _oauth.SessionSource, _oauth.LoginHintSource));

        public IAuthenticator Authenticate()
        {
            var fallback = new FallbackAuthenticator();
            fallback.AddAuthenticatorWithoutValidator(_oauth.Silent());
            fallback.AddAuthenticatorWithoutValidator(Interactive());
            return fallback;
        }

        public IAuthenticator AuthenticateInteractively() => Interactive();
        public IAuthenticator AuthenticateSilently() => _oauth.Silent();
        public IAuthenticator ClearSession() => _oauth.Signout();
        // There is no browser to sign out in: forgetting the tokens is the sign-out.
        public IAuthenticator Signout() => _oauth.Signout();
        public ISessionValidator CreateSessionValidator() => _oauth.Validator();
    }

    private sealed class DeviceCodeOAuth(MicrosoftOAuthParameters parameters) : MicrosoftOAuth(parameters)
    {
        protected override async ValueTask<MicrosoftOAuthResponse?> Authenticate(
            AuthenticateContext context, MicrosoftOAuthParameters parameters)
        {
            var ct = context.CancellationToken;
            using var start = await Http.PostAsync(DeviceCodeUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = parameters.ClientInfo.ClientId,
                ["scope"] = parameters.ClientInfo.Scopes,
                ["response_type"] = "device_code"
            }), ct);
            var startBody = await start.Content.ReadAsStringAsync(ct);
            if (!start.IsSuccessStatusCode)
                throw new InvalidOperationException($"Microsoft would not start a sign-in ({(int)start.StatusCode}): {Describe(startBody)}");

            using var doc = JsonDocument.Parse(startBody);
            var root = doc.RootElement;
            var userCode = root.GetProperty("user_code").GetString() ?? "";
            var deviceCode = root.GetProperty("device_code").GetString() ?? "";
            var uri = root.TryGetProperty("verification_uri", out var v) ? v.GetString() ?? "https://www.microsoft.com/link" : "https://www.microsoft.com/link";
            var interval = root.TryGetProperty("interval", out var i) && i.TryGetInt32(out var iv) ? Math.Max(1, iv) : 5;
            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var ev) ? ev : 900;
            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            AppLog.Log("account", "Microsoft sign-in: waiting for the code to be entered.");
            var show = CodeReady;
            if (show is null) throw new InvalidOperationException("There is nowhere to show the Microsoft sign-in code.");
            try
            {
                await show(new DeviceCodePrompt(userCode, uri, expiresAt), cancel);
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(interval), cancel.Token);
                    if (DateTimeOffset.UtcNow > expiresAt)
                        throw new TimeoutException("The sign-in code expired before it was used. Start the sign-in again.");

                    using var poll = await Http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["client_id"] = parameters.ClientInfo.ClientId,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                        ["device_code"] = deviceCode
                    }), cancel.Token);
                    var body = await poll.Content.ReadAsStringAsync(cancel.Token);
                    if (poll.IsSuccessStatusCode)
                    {
                        AppLog.Log("account", "Microsoft sign-in: code accepted.");
                        return MicrosoftOAuthResponse.FromHttpResponse(body, (int)poll.StatusCode, poll.ReasonPhrase);
                    }

                    var error = ErrorOf(body);
                    switch (error)
                    {
                        case "authorization_pending":
                            continue;
                        case "slow_down":
                            interval += 5;
                            continue;
                        case "authorization_declined":
                        case "access_denied":
                            throw new OperationCanceledException("The sign-in was declined.");
                        case "expired_token":
                        case "bad_verification_code":
                            throw new TimeoutException("The sign-in code expired before it was used. Start the sign-in again.");
                        default:
                            throw new InvalidOperationException($"Microsoft refused the sign-in ({(int)poll.StatusCode}): {Describe(body)}");
                    }
                }
            }
            finally
            {
                Finished?.Invoke();
            }
        }

        private static string? ErrorOf(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : null;
            }
            catch (JsonException) { return null; }
        }

        private static string Describe(string body) => body.Length > 300 ? body[..300] : body;
    }
}
