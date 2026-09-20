namespace CloudLauncher.Shared;

public enum ResourcePackBrowseSource
{
    Public = 0,
    Team = 1,
    Shared = 2,
    Personal = 3
}

/// <param name="VersionCount">How many versions have been uploaded. Zero means the pack page exists
/// but has no file behind it yet, which is the difference between "install" and "nothing to install".</param>
public sealed record HostedResourcePackSummary(
    Guid Id,
    string Slug,
    string Name,
    string? Summary,
    Guid OwnerId,
    string OwnerUsername,
    PackVisibility Visibility,
    string? IconBlobHash,
    string? McVersionsCsv,
    long DownloadCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PackPermissions EffectivePermissions,
    int VersionCount);

public sealed record HostedResourcePackDetail(
    Guid Id,
    string Slug,
    string Name,
    string? Summary,
    string? Description,
    Guid OwnerId,
    string OwnerUsername,
    PackVisibility Visibility,
    string? IconBlobHash,
    string? McVersionsCsv,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PackPermissions EffectivePermissions,
    IReadOnlyList<HostedResourcePackVersionInfo> Versions,
    IReadOnlyList<PackCollaboratorEntry> Collaborators,
    IReadOnlyList<PackTeamEntry> Teams);

public sealed record HostedResourcePackVersionInfo(
    Guid Id,
    string VersionString,
    string? Changelog,
    string ReleaseChannel,
    string BlobHash,
    long FileSize,
    string FileName,
    string? McVersionsCsv,
    DateTimeOffset PublishedAt);

public sealed record ResourcePackBrowsePage(
    IReadOnlyList<HostedResourcePackSummary> Items,
    int Offset,
    int Limit,
    int Total);

public sealed record CreateResourcePackRequest(
    string Name,
    string? Summary,
    string? Description,
    PackVisibility Visibility,
    string? InitialMcVersionsCsv);

/// <summary>Patch for a hosted resource pack. Every property is optional; null leaves the stored value alone.</summary>
/// <param name="McVersionsCsv">Replaces the pack's advertised Minecraft versions.</param>
/// <remarks>
/// Compatibility is editable here because uploading a version was previously the only thing that
/// could write it: a pack created against the wrong Minecraft version could never be corrected.
/// </remarks>
public sealed record UpdateResourcePackRequest(
    string? Name,
    string? Summary,
    string? Description,
    PackVisibility? Visibility,
    string? McVersionsCsv = null);

public sealed record CreateResourcePackVersionRequest(
    string VersionString,
    string? Changelog,
    string ReleaseChannel,
    string FileName,
    string? McVersionsCsv);
