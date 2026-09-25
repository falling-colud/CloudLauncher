using System.IO;
using System.Text.Json;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using XboxAuthNet.Game;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Authenticators;
using XboxAuthNet.Game.OAuth;
using XboxAuthNet.Game.SessionStorages;
using XboxAuthNet.Game.XboxAuth;
using XboxAuthNet.OAuth;
using XboxAuthNet.XboxLive;
using XboxAuthNet.XboxLive.Requests;

namespace CloudLauncher.Services;

public enum MinecraftAccountKind
{
    Offline = 0,
    Microsoft = 1
}

/// <summary>
/// A saved Minecraft account. Microsoft tokens stay in CmlLib's own cache; this only keeps an id
/// and a display name.
/// </summary>
public sealed class StoredMinecraftAccount
{
    public Guid   Id       { get; set; } = Guid.NewGuid();
    public MinecraftAccountKind Kind { get; set; }
    public string Username { get; set; } = "";
    public string? Uuid    { get; set; }
    public string? MicrosoftAccountIdentifier { get; set; }
}

/// <summary>JSON shape stored in <c>minecraft-accounts.json</c>.</summary>
internal sealed class AccountStoreFile
{
    public List<StoredMinecraftAccount> Accounts { get; set; } = new();
    public Guid? CurrentId { get; set; }
}

/// <summary>
/// The saved Minecraft accounts (Microsoft and offline) and which one is current.
/// </summary>
public sealed class MinecraftAccountService
{
    private readonly AppSettings _settings;
    private readonly string _storePath;
    private readonly string _microsoftAccountPath;
    private readonly List<StoredMinecraftAccount> _accounts = new();
    private Guid? _currentId;
    private JELoginHandler? _loginHandler;

    /// <summary>Fired whenever the account list or current selection changes.</summary>
    public event Action? AccountsChanged;

    public MinecraftAccountService(AppSettings settings)
    {
        _settings = settings;
        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CloudLauncher",
            Environment.GetEnvironmentVariable("CL_PROFILE") is { Length: > 0 } p ? p : "default");
        Directory.CreateDirectory(profileDir);
        _storePath = Path.Combine(profileDir, "minecraft-accounts.json");
        _microsoftAccountPath = Path.Combine(profileDir, "minecraft-microsoft-accounts.json");
        Load();
    }

    public IReadOnlyList<StoredMinecraftAccount> Accounts => _accounts;

    public StoredMinecraftAccount? Current =>
        _currentId is { } id ? _accounts.FirstOrDefault(a => a.Id == id) : null;

    public bool IsSignedIn => Current is not null;
    public string DisplayName => Current?.Username ?? "(no account)";

    private JELoginHandler GetLoginHandler() => _loginHandler ??= BuildLoginHandler(_microsoftAccountPath);

    /// <summary>CmlLib's login handler, signing in through the launcher's own Azure application when
    /// <see cref="MicrosoftAuthConfig.ClientId"/> is set and through CmlLib's default client
    /// when not.</summary>
    private static JELoginHandler BuildLoginHandler(string accountPath)
    {
        var builder = new JELoginHandlerBuilder().WithAccountManager(accountPath);
        if (MicrosoftAuthConfig.ClientId is { } clientId && !string.IsNullOrWhiteSpace(clientId))
        {
            builder
                .WithOAuthProvider(new MicrosoftOAuthCodeFlowProvider(
                    new MicrosoftOAuthClientInfo(clientId.Trim(), MicrosoftAuthConfig.Scopes)))
                .WithXboxAuthProvider(new AzureAppXboxProvider(JELoginHandler.RelyingParty));
        }
        return builder.Build();
    }

    // ── adding accounts ──────────────────────────────────────────────────────

    /// <summary>Sign in via Microsoft and add the new account. The result becomes current.</summary>
    public async Task<StoredMinecraftAccount> AddMicrosoftAsync(CancellationToken ct = default)
    {
        AppLog.Log("account", "Starting Microsoft sign-in...");
        var handler = GetLoginHandler();
        var xboxAccount = handler.AccountManager.NewAccount();
        MSession session;
        try
        {
            session = await handler.AuthenticateInteractively(xboxAccount, ct);
            handler.AccountManager.SaveAccounts();
        }
        catch (Exception ex) { AppLog.LogError("ms-signin", ex); throw; }

        if (string.IsNullOrEmpty(session.Username))
            throw new InvalidOperationException("Microsoft sign-in returned no username");

        // Replace an existing Microsoft entry with the same username if one exists
        var existing = _accounts.FirstOrDefault(a =>
            a.Kind == MinecraftAccountKind.Microsoft &&
            string.Equals(a.Username, session.Username, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Uuid = session.UUID;
            existing.MicrosoftAccountIdentifier = xboxAccount.Identifier;
            _currentId = existing.Id;
            AppLog.Log("account", $"Re-signed in to Microsoft account: {session.Username}");
        }
        else
        {
            existing = new StoredMinecraftAccount
            {
                Kind     = MinecraftAccountKind.Microsoft,
                Username = session.Username,
                Uuid     = session.UUID,
                MicrosoftAccountIdentifier = xboxAccount.Identifier
            };
            _accounts.Add(existing);
            _currentId = existing.Id;
            AppLog.Log("account", $"Added Microsoft account: {session.Username}");
        }
        Save();
        AccountsChanged?.Invoke();
        return existing;
    }

    /// <summary>Why a new offline account cannot be added yet, in one sentence for the screen.</summary>
    public const string OfflineNeedsMicrosoftReason =
        "Offline accounts need a Microsoft account signed in here first, because that sign-in shows you own Minecraft: Java Edition.";

    /// <summary>
    /// True when at least one saved Microsoft account has a Minecraft: Java Edition profile.
    /// </summary>
    /// <remarks>
    /// CmlLib's sign-in only completes for accounts that own the game, since its last step reads the
    /// Java profile from <c>api.minecraftservices.com/minecraft/profile</c>. So an account counts when
    /// it has a profile id and CmlLib still holds a session for that profile.
    /// </remarks>
    public bool HasJavaEditionAccount => _accounts.Any(HasJavaProfile);

    private bool HasJavaProfile(StoredMinecraftAccount account)
    {
        if (account.Kind != MinecraftAccountKind.Microsoft || string.IsNullOrWhiteSpace(account.Uuid))
            return false;
        try
        {
            var id = string.IsNullOrEmpty(account.MicrosoftAccountIdentifier)
                ? account.Uuid
                : account.MicrosoftAccountIdentifier;
            return GetLoginHandler().AccountManager.GetAccounts().TryGetAccount(id, out var cached)
                   && cached is JEGameAccount { Profile: { } profile }
                   && string.Equals(profile.UUID, account.Uuid, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLog.LogError("accounts.JavaProfile", ex);
            return false;
        }
    }

    /// <summary>Add an offline account with the given username. Becomes current.</summary>
    /// <remarks>A new offline account needs <see cref="HasJavaEditionAccount"/>. One that is already
    /// saved is selected again, with or without it.</remarks>
    /// <exception cref="InvalidOperationException">No Microsoft account with Java Edition is signed in,
    /// and there is no saved offline account with this name.</exception>
    public StoredMinecraftAccount AddOffline(string username)
    {
        // Validate the trimmed value, or "  ab " would pass the length check.
        username = username?.Trim() ?? "";
        if (username.Length < 3 || username.Length > 16)
            throw new ArgumentException("Offline username must be 3 - 16 characters");

        var existing = _accounts.FirstOrDefault(a =>
            a.Kind == MinecraftAccountKind.Offline &&
            string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _currentId = existing.Id;
        }
        else
        {
            if (!HasJavaEditionAccount)
                throw new InvalidOperationException(OfflineNeedsMicrosoftReason);

            existing = new StoredMinecraftAccount
            {
                Kind = MinecraftAccountKind.Offline,
                Username = username,
                Uuid = null
            };
            _accounts.Add(existing);
            _currentId = existing.Id;
            AppLog.Log("account", $"Added offline account: {username}");
        }
        Save();
        AccountsChanged?.Invoke();
        return existing;
    }

    /// <summary>Renames an offline account.</summary>
    /// <remarks>
    /// Offline only: a Microsoft account's name comes from Mojang and is refreshed on every launch by
    /// <c>UpdateMicrosoftAccountFromSession</c>. The same 3-16 character and duplicate checks as
    /// <see cref="AddOffline"/> apply. Renaming the current account keeps it selected.
    /// </remarks>
    /// <exception cref="ArgumentException">The name is the wrong length, or another offline account
    /// already has it.</exception>
    /// <exception cref="InvalidOperationException">The account is not an offline account.</exception>
    public StoredMinecraftAccount RenameOffline(Guid id, string username)
    {
        var account = _accounts.FirstOrDefault(a => a.Id == id)
            ?? throw new InvalidOperationException("That account is no longer in the list.");
        if (account.Kind != MinecraftAccountKind.Offline)
            throw new InvalidOperationException(
                "Microsoft accounts are named by Mojang - the name comes back from the sign-in every launch.");

        username = username?.Trim() ?? "";
        if (username.Length < 3 || username.Length > 16)
            throw new ArgumentException("Offline username must be 3 - 16 characters");

        if (string.Equals(account.Username, username, StringComparison.Ordinal))
            return account; // nothing changed: skip the write and the change event

        var clash = _accounts.FirstOrDefault(a =>
            a.Id != id &&
            a.Kind == MinecraftAccountKind.Offline &&
            string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
            throw new ArgumentException($"There is already an offline account called {clash.Username}.");

        AppLog.Log("account", $"Renamed offline account: {account.Username} -> {username}");
        account.Username = username;
        Save();
        AccountsChanged?.Invoke();
        return account;
    }

    /// <summary>Switch the current selection to the given account ID.</summary>
    public void Switch(Guid id)
    {
        var match = _accounts.FirstOrDefault(a => a.Id == id);
        if (match is null) return;
        _currentId = id;
        AppLog.Log("account", $"Switched to {match.Kind} account: {match.Username}");
        Save();
        AccountsChanged?.Invoke();
    }

    /// <summary>Remove the account. If it was current, pick another (or null).</summary>
    public async Task RemoveAsync(Guid id)
    {
        var match = _accounts.FirstOrDefault(a => a.Id == id);
        if (match is null) return;
        _accounts.Remove(match);
        AppLog.Log("account", $"Removed account: {match.Username}");
        if (_currentId == id) _currentId = _accounts.FirstOrDefault()?.Id;

        // If we removed a Microsoft account and no other Microsoft accounts remain,
        // also clear CmlLib's cache so the next sign-in is a fresh login.
        if (match.Kind == MinecraftAccountKind.Microsoft &&
            !_accounts.Any(a => a.Kind == MinecraftAccountKind.Microsoft))
        {
            try { if (_loginHandler is not null) await _loginHandler.Signout(); } catch { /* best effort */ }
        }
        Save();
        AccountsChanged?.Invoke();
    }

    // ── session for launch ───────────────────────────────────────────────────

    /// <summary>Build a launch session for the current account.</summary>
    public async Task<MSession> GetLaunchSessionAsync(CancellationToken ct = default)
    {
        var current = Current
            ?? throw new InvalidOperationException("No Minecraft account configured - add one from the title-bar chip.");

        if (current.Kind == MinecraftAccountKind.Offline)
        {
            AppLog.Log("account", $"Using offline session for {current.Username}");
            return MSession.CreateOfflineSession(current.Username);
        }

        AppLog.Log("account", $"Refreshing Microsoft session for {current.Username}...");
        var handler = GetLoginHandler();
        var xboxAccount = ResolveXboxAccount(handler, current);
        try
        {
            var session = xboxAccount is null
                ? await ReauthenticateMicrosoftAsync(handler, current, ct)
                : await handler.AuthenticateSilently(xboxAccount, ct);
            UpdateMicrosoftAccountFromSession(current, xboxAccount, session);
            AppLog.Log("account", $"Microsoft session ready: {session.Username}");
            return session;
        }
        catch (Exception ex)
        {
            AppLog.LogError("silent-auth", ex);
            // Fall back to interactive if silent fails
            AppLog.Log("account", "Silent auth failed; prompting for sign-in...");
            var session = xboxAccount is null
                ? await ReauthenticateMicrosoftAsync(handler, current, ct)
                : await handler.AuthenticateInteractively(xboxAccount, ct);
            UpdateMicrosoftAccountFromSession(current, xboxAccount, session);
            return session;
        }
    }

    /// <summary>Signs out and clears every saved account, Microsoft tokens included.</summary>
    public async Task SignOutAllAsync()
    {
        try { if (_loginHandler is not null) await _loginHandler.Signout(); } catch { /* best effort */ }
        _accounts.Clear();
        _currentId = null;
        Save();
        AccountsChanged?.Invoke();
    }

    private static IXboxGameAccount? ResolveXboxAccount(JELoginHandler handler, StoredMinecraftAccount account)
    {
        if (string.IsNullOrEmpty(account.MicrosoftAccountIdentifier))
            return null;

        return handler.AccountManager.GetAccounts()
            .TryGetAccount(account.MicrosoftAccountIdentifier, out var xboxAccount)
            ? xboxAccount
            : null;
    }

    private async Task<MSession> ReauthenticateMicrosoftAsync(
        JELoginHandler handler,
        StoredMinecraftAccount account,
        CancellationToken ct)
    {
        AppLog.Log("account", $"Microsoft token for {account.Username} is missing; prompting for sign-in...");
        var xboxAccount = handler.AccountManager.NewAccount();
        var session = await handler.AuthenticateInteractively(xboxAccount, ct);
        UpdateMicrosoftAccountFromSession(account, xboxAccount, session);
        return session;
    }

    private void UpdateMicrosoftAccountFromSession(
        StoredMinecraftAccount account,
        IXboxGameAccount? xboxAccount,
        MSession session)
    {
        var changed = false;
        var username = string.IsNullOrWhiteSpace(session.Username) ? account.Username : session.Username;
        if (!string.Equals(account.Username, username, StringComparison.Ordinal))
        {
            account.Username = username;
            changed = true;
        }
        if (!string.Equals(account.Uuid, session.UUID, StringComparison.Ordinal))
        {
            account.Uuid = session.UUID;
            changed = true;
        }
        if (xboxAccount is not null &&
            !string.Equals(account.MicrosoftAccountIdentifier, xboxAccount.Identifier, StringComparison.Ordinal))
        {
            account.MicrosoftAccountIdentifier = xboxAccount.Identifier;
            changed = true;
        }
        if (!changed) return;
        handlerSaveSafe();
        Save();
        AccountsChanged?.Invoke();

        void handlerSaveSafe()
        {
            try { GetLoginHandler().AccountManager.SaveAccounts(); }
            catch (Exception ex) { AppLog.LogError("accounts.SaveMicrosoft", ex); }
        }
    }

    // ── persistence ──────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            // New format
            if (File.Exists(_storePath))
            {
                var file = JsonSerializer.Deserialize<AccountStoreFile>(File.ReadAllText(_storePath));
                if (file is not null)
                {
                    _accounts.AddRange(file.Accounts);
                    _currentId = file.CurrentId;
                    if (_currentId is null || !_accounts.Any(a => a.Id == _currentId))
                        _currentId = _accounts.FirstOrDefault()?.Id;
                    return;
                }
            }

            // Migrate from the old single-account file if present
            var legacyPath = Path.Combine(Path.GetDirectoryName(_storePath)!, "minecraft-account.json");
            if (File.Exists(legacyPath))
            {
                var single = JsonSerializer.Deserialize<StoredMinecraftAccount>(File.ReadAllText(legacyPath));
                if (single is not null && !string.IsNullOrEmpty(single.Username))
                {
                    if (single.Id == Guid.Empty) single.Id = Guid.NewGuid();
                    _accounts.Add(single);
                    _currentId = single.Id;
                    Save();
                    try { File.Delete(legacyPath); } catch { }
                }
            }
        }
        catch (Exception ex) { AppLog.LogError("accounts.Load", ex); }
    }

    private void Save()
    {
        try
        {
            var file = new AccountStoreFile { Accounts = _accounts, CurrentId = _currentId };
            File.WriteAllText(_storePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { AppLog.LogError("accounts.Save", ex); }
    }

    // ── the launcher's own Azure application ─────────────────────────────────

    /// <summary>
    /// Xbox Live sign-in for a Microsoft token issued to an Azure application: CmlLib's basic Xbox
    /// steps with the user-token request swapped for <see cref="AzureUserTokenAuth"/>.
    /// </summary>
    private sealed class AzureAppXboxProvider(string relyingParty) : XboxAuthenticationProviderBase(relyingParty)
    {
        protected override IAuthenticator CreateAuthenticator()
        {
            var steps = new AuthenticatorCollection();
            steps.AddAuthenticatorWithoutValidator(new AzureUserTokenAuth(Builder.OAuthSessionSource, Builder.SessionSource));
            steps.AddAuthenticatorWithoutValidator(Builder.XstsTokenAuth());
            if (Builder.UseXuiClaimsAuth)
                steps.AddAuthenticatorWithoutValidator(Builder.XuiClaimsAuth());
            return steps;
        }
    }

    /// <summary>Trades the Microsoft token for an Xbox user token. Xbox wants a token issued to an
    /// Azure application sent with the "d=" prefix; the stock step sends it bare, which only works
    /// for CmlLib's default client.</summary>
    private sealed class AzureUserTokenAuth(
        ISessionSource<MicrosoftOAuthResponse> oauthSource,
        ISessionSource<XboxAuthTokens> sessionSource) : SessionAuthenticator<XboxAuthTokens>(sessionSource)
    {
        protected override async ValueTask<XboxAuthTokens?> Authenticate(AuthenticateContext context)
        {
            var token = oauthSource.Get(context.SessionStorage)?.AccessToken;
            if (string.IsNullOrEmpty(token))
                throw new XboxAuthException("OAuth access token was empty. Microsoft OAuth is required.", 0);

            var rps = token.StartsWith("d=", StringComparison.Ordinal) || token.StartsWith("t=", StringComparison.Ordinal)
                ? token
                : XboxAuthConstants.AzureTokenPrefix + token;
            var userToken = await new XboxAuthClient(context.HttpClient)
                .RequestUserToken(new XboxUserTokenRequest { AccessToken = rps })
                .ConfigureAwait(false);

            var tokens = GetSessionFromStorage() ?? new XboxAuthTokens();
            tokens.UserToken = userToken;
            return tokens;
        }
    }
}
