namespace CloudLauncher.Shared;

/// <summary>Metadata describing the latest published launcher build available on the server.
/// The actual package (a zip of the launcher's publish output) is fetched separately.</summary>
public sealed record LauncherReleaseInfo(
    string Version,
    string FileName,
    long Size,
    string Sha256,
    string? Notes,
    DateTimeOffset ReleasedAt);
