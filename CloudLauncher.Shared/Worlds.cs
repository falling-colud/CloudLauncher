namespace CloudLauncher.Shared;

public enum WorldBrowseSource
{
    Public = 0,
    Team = 1,
    Shared = 2,
    Personal = 3
}

public sealed record SharedWorldSummary(
    Guid Id,
    string Slug,
    string Name,
    string? Summary,
    Guid OwnerId,
    string OwnerUsername,
    PackVisibility Visibility,
    string? IconBlobHash,
    string? McVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PackPermissions EffectivePermissions);

public sealed record SharedWorldDetail(
    Guid Id,
    string Slug,
    string Name,
    string? Summary,
    string? Description,
    Guid OwnerId,
    string OwnerUsername,
    PackVisibility Visibility,
    string? IconBlobHash,
    string? McVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PackPermissions EffectivePermissions,
    IReadOnlyList<SharedWorldVersionInfo> Versions,
    IReadOnlyList<PackCollaboratorEntry> Collaborators,
    IReadOnlyList<PackTeamEntry> Teams);

public sealed record SharedWorldVersionInfo(
    Guid Id,
    string VersionString,
    string? Changelog,
    string BlobHash,
    long FileSize,
    string FileName,
    string? McVersion,
    DateTimeOffset PublishedAt);

public sealed record WorldBrowsePage(
    IReadOnlyList<SharedWorldSummary> Items,
    int Offset,
    int Limit,
    int Total);

public sealed record CreateWorldRequest(
    string Name,
    string? Summary,
    string? Description,
    PackVisibility Visibility,
    string? McVersion);

public sealed record UpdateWorldRequest(
    string? Name,
    string? Summary,
    string? Description,
    PackVisibility? Visibility);

public sealed record CreateWorldVersionRequest(
    string VersionString,
    string? Changelog,
    string FileName,
    string? McVersion);
