namespace CloudLauncher.Shared;

/// <summary>Metadata for the latest launcher build on the server; the package (a zip of the
/// publish output) is fetched separately.</summary>
/// <remarks>The <c>Installer*</c> fields describe the optional setup .exe served at
/// <c>/download</c>. They are optional for older stored metadata and older clients, and self-update
/// ignores them. <c>Manifest</c> is the base64 of the signed text (format, version, sha256 and size of
/// the package), <c>Signature</c> an ECDSA P-256 / SHA-256 signature over it (base64, DER), and
/// <c>KeyId</c> the public key compiled into the launcher that must verify it.</remarks>
public sealed record LauncherReleaseInfo(
    string Version,
    string FileName,
    long Size,
    string Sha256,
    string? Notes,
    DateTimeOffset ReleasedAt,
    string? InstallerFileName = null,
    long InstallerSize = 0,
    string? InstallerSha256 = null,
    string? Manifest = null,
    string? Signature = null,
    string? KeyId = null);
