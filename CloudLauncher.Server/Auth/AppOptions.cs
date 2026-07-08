namespace CloudLauncher.Server.Auth;

public sealed class AppOptions
{
    /// <summary>Public URL of this server (used in email links and Google OAuth redirect).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5000";

    /// <summary>Username granted admin during first-run bootstrap — but ONLY while no admin
    /// account exists yet. Once any admin exists this setting is inert, so it is not a standing
    /// privilege-escalation backdoor. Override with App:BootstrapAdminUsername; set empty to
    /// disable bootstrap entirely (seed the first admin out-of-band instead).</summary>
    public string BootstrapAdminUsername { get; set; } = "colud";
}

public sealed class GoogleAuthOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public sealed class EmailOptions
{
    public string FromAddress { get; set; } = "noreply@cloudlauncher.local";
    public string FromName { get; set; } = "CloudLauncher";
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUser { get; set; }
    public string? SmtpPassword { get; set; }
    public bool UseSsl { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(SmtpHost);
}
