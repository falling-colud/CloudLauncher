using System.Text.Json.Serialization;

namespace CloudLauncher.Services;

public sealed partial class AppSettings
{
    /// <summary>The account that <c>auth/me</c> last reported as an administrator, or null.</summary>
    /// <remarks>Stored as the account id rather than a flag, so it stops applying when another
    /// account signs in or the user signs out, without either path having to clear it.</remarks>
    public Guid? AdminAccountId { get; set; }

    /// <summary>True when the signed-in account is an administrator, as of the last <c>auth/me</c>.</summary>
    [JsonIgnore]
    public bool IsAdmin => AdminAccountId is { } id && id == UserId;

    /// <summary>The account this PC has seen use a password (to sign in, register or change it), or
    /// null.</summary>
    /// <remarks>An account that only ever signed in here with Google may still have a password, so
    /// the absence of this says "unknown", not "no password".</remarks>
    public Guid? PasswordAccountId { get; set; }

    /// <summary>True when the signed-in account is known to have a password.</summary>
    [JsonIgnore]
    public bool AccountHasPassword => PasswordAccountId is { } id && id == UserId;

    /// <summary>Set once the Internet Explorer feature-control values for this exe have been removed
    /// from the registry for this profile.</summary>
    public bool LegacyBrowserKeysRemoved { get; set; }
}
