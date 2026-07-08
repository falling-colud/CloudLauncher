namespace CloudLauncher.Shared;

public enum ModBrowseSource
{
    Public = 0,
    Team = 1,
    Shared = 2,
    Personal = 3
}

public sealed record HostedModSummary(
    Guid Id,
    string Slug,
    string Name,
    string? Summary,
    Guid OwnerId,
    string OwnerUsername,
    PackVisibility Visibility,
    string? IconBlobHash,
    string? McVersionsCsv,
    string? LoadersCsv,
    long DownloadCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PackPermissions EffectivePermissions);

public sealed record HostedModDetail(
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
    string? LoadersCsv,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PackPermissions EffectivePermissions,
    IReadOnlyList<HostedModVersionInfo> Versions,
    IReadOnlyList<PackCollaboratorEntry> Collaborators,
    IReadOnlyList<PackTeamEntry> Teams);

public sealed record HostedModVersionInfo(
    Guid Id,
    string VersionString,
    string? Changelog,
    string ReleaseChannel,
    string BlobHash,
    long FileSize,
    string FileName,
    string? McVersionsCsv,
    string? LoadersCsv,
    DateTimeOffset PublishedAt);

public sealed record ModBrowsePage(
    IReadOnlyList<HostedModSummary> Items,
    int Offset,
    int Limit,
    int Total);

public sealed record CreateModRequest(
    string Name,
    string? Summary,
    string? Description,
    PackVisibility Visibility,
    string? InitialMcVersionsCsv,
    string? InitialLoadersCsv);

public sealed record UpdateModRequest(
    string? Name,
    string? Summary,
    string? Description,
    PackVisibility? Visibility);

public sealed record CreateModVersionRequest(
    string VersionString,
    string? Changelog,
    string ReleaseChannel,
    string FileName,
    string? McVersionsCsv,
    string? LoadersCsv);
