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
/// <param name="LoadersCsv">Replaces the mod's advertised loaders; normalised to lower case server-side.</param>
/// <remarks>
/// The two compatibility fields are editable here because uploading a version was previously the
/// only thing that could write them: a mod created against the wrong Minecraft version stayed wrong
/// forever, and the only way to correct it was to upload a file that said otherwise.
/// </remarks>
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
/// <remarks>
/// A version could previously only be uploaded or deleted. Correcting a typo in the version string,
/// a changelog written in a hurry, or a channel set to "release" by accident therefore meant deleting
/// the file and uploading the same bytes again under a new id — which broke every link to the old one
/// and lost its publish date. Patching leaves the blob, the id and the publish date alone and edits
/// only what the owner typed.
/// </remarks>
public sealed record UpdateModVersionRequest(
    string? VersionString = null,
    string? Changelog = null,
    string? ReleaseChannel = null,
    string? McVersionsCsv = null,
    string? LoadersCsv = null);

/// <summary>How <c>GET /mods/browse</c> orders its results.</summary>
/// <remarks>
/// Sent as a plain string rather than an enum so an older server that does not know the parameter
/// ignores it, and a newer client asking for a mode this server has never heard of falls back to the
/// default instead of erroring. Only the CloudLauncher sources honour it — CurseForge and Modrinth
/// results arrive in the store's own order.
/// </remarks>
public static class ModBrowseSort
{
    public const string Updated = "updated";
    public const string Created = "created";
    public const string Name = "name";
    public const string Downloads = "downloads";
}
