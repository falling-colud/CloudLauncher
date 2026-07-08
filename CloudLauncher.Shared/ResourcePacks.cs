namespace CloudLauncher.Shared;

public enum ResourcePackBrowseSource
{
    Public = 0,
    Team = 1,
    Shared = 2,
    Personal = 3
}

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
    PackPermissions EffectivePermissions);

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

public sealed record UpdateResourcePackRequest(
    string? Name,
    string? Summary,
    string? Description,
    PackVisibility? Visibility);

public sealed record CreateResourcePackVersionRequest(
    string VersionString,
    string? Changelog,
    string ReleaseChannel,
    string FileName,
    string? McVersionsCsv);
