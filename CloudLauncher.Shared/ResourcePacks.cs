namespace CloudLauncher.Shared;

public enum ResourcePackBrowseSource
{
    Public = 0,
    Team = 1,
    Shared = 2,
    Personal = 3
}

/// <param name="VersionCount">Number of uploaded versions. Zero means the pack exists but has nothing
/// to install yet.</param>
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

/// <summary>Patch for a hosted resource pack. Every property is optional; null leaves the stored
/// value alone.</summary>
/// <param name="McVersionsCsv">Replaces the pack's advertised Minecraft versions, so a wrong one can be
/// fixed without uploading a new version.</param>
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
