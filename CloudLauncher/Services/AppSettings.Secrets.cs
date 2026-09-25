using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

// The session and RCON passwords live in SecretStore (DPAPI), like the CurseForge key, because
// settings.json is plain text that ends up in bug reports and backups, and any launcher process
// that saves rewrites all of it. The settings.json slots are only read to migrate values from older
// builds, and stay empty unless the secret store can't be written on this PC.
public sealed partial class AppSettings
{
    private const string SessionSecret = "session";
    private const string RconSecret = "rcon-passwords";

    private string? _accessToken;
    private string? _refreshToken;
    private string? _legacyAccessToken;
    private string? _legacyRefreshToken;
    private bool _sessionLivesInSettings;

    private bool _secretsLoaded;
    private Dictionary<string, string> _persistedRcon = new(StringComparer.Ordinal);
    private bool _rconLivesInSettings;

    // ── transport ───────────────────────────────────────────────────────────

    /// <summary>
    /// False while <see cref="ServerUrl"/> is plain http to another machine. Tokens and passwords are
    /// never sent there: <see cref="AccessToken"/> and <see cref="RefreshToken"/> read as null.
    /// </summary>
    [JsonIgnore]
    public bool CanSendCredentials => IsSecureServerUrl(ServerUrl);

    /// <summary>True for https, and for plain http to this machine (what the test harnesses use).</summary>
    public static bool IsSecureServerUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && IsSecureServerUri(uri);

    /// <inheritdoc cref="IsSecureServerUrl"/>
    public static bool IsSecureServerUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri));

    /// <summary>True when <paramref name="uri"/> names this machine.</summary>
    public static bool IsLoopbackHost(Uri uri) =>
        uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    // ── session ─────────────────────────────────────────────────────────────

    /// <summary>The launcher account's access token, or null when signed out.</summary>
    /// <remarks>Kept in <see cref="SecretStore"/>; only <see cref="SetSessionTokens"/> changes it.
    /// Null as well while <see cref="CanSendCredentials"/> is false.</remarks>
    [JsonIgnore]
    public string? AccessToken => CanSendCredentials ? _accessToken : null;

    /// <summary>The launcher account's refresh token, or null when signed out.</summary>
    /// <remarks>Same storage and the same transport rule as <see cref="AccessToken"/>.</remarks>
    [JsonIgnore]
    public string? RefreshToken => CanSendCredentials ? _refreshToken : null;

    /// <summary>True when a session is stored, whether or not the server address lets it be used.</summary>
    [JsonIgnore]
    public bool HasStoredSession => _accessToken is not null || _refreshToken is not null;

    /// <summary>The settings.json slot the access token was kept in, in plain text, by older builds.</summary>
    [JsonPropertyName("AccessToken")]
    public string? LegacyAccessToken
    {
        get => _sessionLivesInSettings ? _accessToken : null;
        set => _legacyAccessToken = value;
    }

    /// <summary>The settings.json slot the refresh token was kept in, in plain text, by older builds.</summary>
    [JsonPropertyName("RefreshToken")]
    public string? LegacyRefreshToken
    {
        get => _sessionLivesInSettings ? _refreshToken : null;
        set => _legacyRefreshToken = value;
    }

    /// <summary>
    /// Stores (or, with nulls, forgets) the session's token pair at once. Returns false when the secret
    /// store could not be written; the pair is then kept in settings.json on the next save instead.
    /// </summary>
    public bool SetSessionTokens(string? accessToken, string? refreshToken)
    {
        // A sign-in and a background refresh must not write the secret file at the same moment.
        lock (_sessionGate)
        {
            _accessToken = NullIfBlank(accessToken);
            _refreshToken = NullIfBlank(refreshToken);
            var stored = WriteSession();
            _sessionLivesInSettings = !stored && HasStoredSession;
            return stored;
        }
    }

    private readonly object _sessionGate = new();

    // Both tokens in one secret, so a crash between two writes cannot pair a new access token with
    // an old refresh token.
    private bool WriteSession() => SecretStore.Write(SessionSecret,
        HasStoredSession ? JsonSerializer.Serialize(new SessionSecretData(_accessToken, _refreshToken)) : null);

    private sealed record SessionSecretData(string? AccessToken, string? RefreshToken);

    // ── loading ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the session and RCON passwords from <see cref="SecretStore"/>, moving any still sitting
    /// in settings.json into it first.
    /// </summary>
    /// <returns>True when settings.json held a secret in plain text, so the caller saves it back
    /// without.</returns>
    private bool LoadSecrets()
    {
        var plainText = LoadSession();
        plainText |= LoadRconPasswords();
        _secretsLoaded = true;
        return plainText;
    }

    private bool LoadSession()
    {
        var legacyAccess = NullIfBlank(_legacyAccessToken);
        var legacyRefresh = NullIfBlank(_legacyRefreshToken);
        _legacyAccessToken = _legacyRefreshToken = null;

        if (legacyAccess is not null || legacyRefresh is not null)
        {
            // Only builds without the secret store write these, so they are the newest sign-in and match
            // the expiry and user id stored next to them.
            _accessToken = legacyAccess;
            _refreshToken = legacyRefresh;
            _sessionLivesInSettings = !WriteSession();
            return true;
        }

        if (SecretStore.Read(SessionSecret) is not { } stored) return false;
        try
        {
            var data = JsonSerializer.Deserialize<SessionSecretData>(stored);
            _accessToken = NullIfBlank(data?.AccessToken);
            _refreshToken = NullIfBlank(data?.RefreshToken);
        }
        catch (JsonException ex)
        {
            // Unreadable is signed out, which the login screen already handles.
            AppLog.LogError("secrets", ex);
        }
        return false;
    }

    // A password found in settings.json is moved by the save Load() makes when this returns true.
    private bool LoadRconPasswords()
    {
        var stored = ReadRconSecret();
        var plainText = false;
        foreach (var (key, entry) in ServerAdmins)
        {
            // A password in settings.json came from an older build after this one last saved, so it wins
            // over the stored one.
            if (!string.IsNullOrEmpty(entry.RconPassword)) { plainText = true; continue; }
            if (stored.TryGetValue(key, out var password)) entry.RconPassword = password;
        }
        _persistedRcon = stored;
        return plainText;
    }

    private static Dictionary<string, string> ReadRconSecret()
    {
        if (SecretStore.Read(RconSecret) is not { } json) return new(StringComparer.Ordinal);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return map is null ? new(StringComparer.Ordinal) : new(map, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            AppLog.LogError("secrets", ex);
            return new(StringComparer.Ordinal);
        }
    }

    // ── saving ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the RCON passwords to <see cref="SecretStore"/> when they differ from what was last
    /// written. Called by <see cref="Save"/>, under its lock, before settings.json is serialized.
    /// </summary>
    /// <remarks>Passwords are set on <see cref="ServerAdminEntry"/> objects by the console setup, which
    /// knows nothing of the store, so the save is where they are picked up. Settings that never loaded
    /// their secrets (a bare <c>new AppSettings()</c>) write nothing, or they would wipe the store.</remarks>
    private void PersistRconPasswords()
    {
        if (!_secretsLoaded) return;

        try
        {
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, entry) in ServerAdmins)
                if (!string.IsNullOrEmpty(entry.RconPassword)) current[key] = entry.RconPassword;

            if (!SameEntries(current, _persistedRcon))
            {
                var stored = SecretStore.Write(RconSecret, current.Count == 0 ? null : JsonSerializer.Serialize(current));
                _rconLivesInSettings = !stored && current.Count > 0;
                _persistedRcon = current;
            }

            foreach (var entry in ServerAdmins.Values) entry.PasswordInSettingsFile = _rconLivesInSettings;
        }
        catch (InvalidOperationException)
        {
            // Changed on another thread mid-read; the next save picks it up.
        }
    }

    private static bool SameEntries(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.Ordinal));

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
