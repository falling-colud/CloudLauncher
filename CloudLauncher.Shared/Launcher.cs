namespace CloudLauncher.Shared;

/// <summary>Metadata describing the latest published launcher build available on the server.
/// The actual package (a zip of the launcher's publish output) is fetched separately.
///
/// The <c>Installer*</c> fields describe the optional Windows setup .exe served to human
/// downloaders at <c>/download</c>. They are nullable/defaulted so older stored metadata and
/// older clients (which only know the first six fields) remain compatible — the self-update
/// path never reads them.</summary>
public sealed record LauncherReleaseInfo(
    string Version,
    string FileName,
    long Size,
    string Sha256,
    string? Notes,
    DateTimeOffset ReleasedAt,
    string? InstallerFileName = null,
    long InstallerSize = 0,
    string? InstallerSha256 = null);
