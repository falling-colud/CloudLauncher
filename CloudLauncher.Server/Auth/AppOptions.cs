namespace CloudLauncher.Server.Auth;

public sealed class AppOptions
{
    /// <summary>Public URL of this server (used in email links and Google OAuth redirect).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5000";
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
