namespace CloudLauncher.Services;

/// <summary>The Microsoft (Azure) application the launcher signs in to Minecraft with.</summary>
/// <remarks>While <see cref="ClientId"/> is null, sign-in uses CmlLib's built-in client. The
/// application needs <c>https://login.live.com/oauth20_desktop.srf</c> as a redirect URI and personal
/// Microsoft accounts allowed.</remarks>
public static class MicrosoftAuthConfig
{
    // Must be set to the launcher's own Azure application id once Microsoft approves it for the Minecraft API.
    public static string? ClientId { get; set; }

    /// <summary>What an Azure application asks Microsoft for. Only used when <see cref="ClientId"/> is set.</summary>
    public const string Scopes = "XboxLive.signin offline_access";
}
