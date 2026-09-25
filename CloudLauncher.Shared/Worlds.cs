namespace CloudLauncher.Shared;

public enum WorldBrowseSource
{
    Public = 0,
    Team = 1,
    Shared = 2,
    Personal = 3
}

/// <param name="VersionCount">How many versions have been uploaded. Zero means the world page exists
/// but has no save behind it yet, which is the difference between "install" and "nothing to install".</param>
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
    PackPermissions EffectivePermissions,
    int VersionCount);

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

/// <summary>Patch for a shared world. Every property is optional; null leaves the stored value
/// alone.</summary>
/// <param name="McVersion">Replaces the world's advertised Minecraft version.</param>
/// <remarks>Editable here so a world created with the wrong Minecraft version can be corrected
/// without uploading a new save.</remarks>
public sealed record UpdateWorldRequest(
    string? Name,
    string? Summary,
    string? Description,
    PackVisibility? Visibility,
    string? McVersion = null);

public sealed record CreateWorldVersionRequest(
    string VersionString,
    string? Changelog,
    string FileName,
    string? McVersion);
