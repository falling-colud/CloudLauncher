namespace CloudLauncher.Shared;

public enum ModBrowseSource
{
    Public = 0,
    Team = 1,
    Shared = 2,
    Personal = 3
}

/// <param name="VersionCount">How many versions have been uploaded. Zero means the mod page exists
/// but has no file behind it yet, which is the difference between "install" and "nothing to install".</param>
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
    PackPermissions EffectivePermissions,
    int VersionCount);

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

/// <summary>Patch for a hosted mod. Every property is optional; null leaves the stored value alone.</summary>
/// <param name="McVersionsCsv">Replaces the mod's advertised Minecraft versions.</param>
/// <param name="LoadersCsv">Replaces the mod's advertised loaders; normalised to lower case
/// server-side.</param>
/// <remarks>Compatibility is editable here so a mod created with the wrong Minecraft version can be
/// corrected without uploading a new file.</remarks>
public sealed record UpdateModRequest(
    string? Name,
    string? Summary,
    string? Description,
    PackVisibility? Visibility,
    string? McVersionsCsv = null,
    string? LoadersCsv = null);

public sealed record CreateModVersionRequest(
    string VersionString,
    string? Changelog,
    string ReleaseChannel,
    string FileName,
    string? McVersionsCsv,
    string? LoadersCsv);

/// <summary>Patch for one uploaded version of a hosted mod. Every property is optional; null leaves
/// the stored value alone.</summary>
/// <remarks>Edits metadata in place, keeping the blob, id and publish date, so fixing a typo doesn't
/// mean re-uploading the file under a new id.</remarks>
public sealed record UpdateModVersionRequest(
    string? VersionString = null,
    string? Changelog = null,
    string? ReleaseChannel = null,
    string? McVersionsCsv = null,
    string? LoadersCsv = null);

/// <summary>How <c>GET /mods/browse</c> orders its results.</summary>
/// <remarks>A plain string rather than an enum, so older servers ignore it and unknown modes fall
/// back to the default instead of erroring. Only the CloudLauncher sources honour it; CurseForge
/// and Modrinth results keep the store's own order.</remarks>
public static class ModBrowseSort
{
    public const string Updated = "updated";
    public const string Created = "created";
    public const string Name = "name";
    public const string Downloads = "downloads";
}
