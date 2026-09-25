namespace CloudLauncher.Shared;

/// <summary>What kind of content a <see cref="ContentBundleSummary"/> holds.</summary>
/// <remarks>Mods, worlds and resource packs have their own tables. Everything else shares the
/// bundle family and is told apart by this enum plus
/// <see cref="ContentBundleSummary.TargetPathRoot"/>, since a shader pack and a kubejs folder
/// differ only in where they unpack.</remarks>
public enum BundleKind
{
    ShaderPack = 0,
    ConfigBundle = 1,
    KubeJsBundle = 2,
    DataPack = 3,
    Other = 99
}

public enum BundleBrowseSource
{
    Public = 0,
    Team = 1,
    Shared = 2,
    Personal = 3
}

/// <summary>Default unpack folder inside an instance for each <see cref="BundleKind"/>.</summary>
/// <remarks>A bundle stores its own <c>TargetPathRoot</c>, so an owner can point it elsewhere; this
/// is the default a create form offers.</remarks>
public static class BundleTargets
{
    public const string ShaderPacks = "shaderpacks";
    public const string Config = "config";
    public const string KubeJs = "kubejs";
    public const string DataPacks = "datapacks";

    public static string DefaultFor(BundleKind kind) => kind switch
    {
        BundleKind.ShaderPack => ShaderPacks,
        BundleKind.ConfigBundle => Config,
        BundleKind.KubeJsBundle => KubeJs,
        BundleKind.DataPack => DataPacks,
        _ => ""
    };
}

/// <param name="TargetPathRoot">Relative folder inside an instance this bundle unpacks into
/// ("shaderpacks", "config", "kubejs", "datapacks"). Empty means the instance root.</param>
/// <param name="VersionCount">How many versions have been uploaded. Zero means the bundle exists
/// but has nothing to install yet.</param>
public sealed record ContentBundleSummary(
    Guid Id,
    BundleKind Kind,
    string Slug,
    string Name,
    string? Summary,
    Guid OwnerId,
    string OwnerUsername,
    PackVisibility Visibility,
    string? IconBlobHash,
    string TargetPathRoot,
    string? McVersionsCsv,
    string? LoadersCsv,
    long DownloadCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PackPermissions EffectivePermissions,
    int VersionCount);

/// <param name="ShareToken">The bundle's current share link token, or null when there is none. Only
/// filled in for callers who may manage the bundle, since the token grants access.</param>
public sealed record ContentBundleDetail(
    Guid Id,
    BundleKind Kind,
    string Slug,
    string Name,
    string? Summary,
    string? Description,
    Guid OwnerId,
    string OwnerUsername,
    PackVisibility Visibility,
    string? IconBlobHash,
    string TargetPathRoot,
    string? McVersionsCsv,
    string? LoadersCsv,
    long DownloadCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PackPermissions EffectivePermissions,
    IReadOnlyList<ContentBundleVersionInfo> Versions,
    IReadOnlyList<PackCollaboratorEntry> Collaborators,
    IReadOnlyList<PackTeamEntry> Teams,
    string? ShareToken = null);

public sealed record ContentBundleVersionInfo(
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

public sealed record BundleBrowsePage(
    IReadOnlyList<ContentBundleSummary> Items,
    int Offset,
    int Limit,
    int Total);

/// <param name="TargetPathRoot">Null asks the server for <see cref="BundleTargets.DefaultFor"/>.</param>
public sealed record CreateBundleRequest(
    BundleKind Kind,
    string Name,
    string? Summary,
    string? Description,
    PackVisibility Visibility,
    string? TargetPathRoot = null,
    string? InitialMcVersionsCsv = null,
    string? InitialLoadersCsv = null);

/// <summary>Patch for a content bundle. Every property is optional; null leaves the stored value
/// alone.</summary>
/// <remarks><c>Kind</c> can't be patched: it is part of the bundle's unique key, so changing it
/// could collide with an existing slug. Delete and re-create instead.</remarks>
public sealed record UpdateBundleRequest(
    string? Name,
    string? Summary,
    string? Description,
    PackVisibility? Visibility,
    string? TargetPathRoot = null,
    string? McVersionsCsv = null,
    string? LoadersCsv = null);

public sealed record CreateBundleVersionRequest(
    string VersionString,
    string? Changelog,
    string ReleaseChannel,
    string FileName,
    string? McVersionsCsv,
    string? LoadersCsv);

/// <summary>Patch for one uploaded version. Null leaves the stored value alone.</summary>
/// <remarks>The blob, id and publish date never change, so fixing a typo in a version string keeps
/// links to it and its release date.</remarks>
public sealed record UpdateBundleVersionRequest(
    string? VersionString = null,
    string? Changelog = null,
    string? ReleaseChannel = null,
    string? McVersionsCsv = null,
    string? LoadersCsv = null);

/// <summary>How <c>GET /bundles/browse</c> orders its results.</summary>
/// <remarks>Sent as a plain string for the same reason <see cref="ModBrowseSort"/> is: an older
/// server ignores a mode it has never heard of instead of erroring.</remarks>
public static class BundleBrowseSort
{
    public const string Updated = "updated";
    public const string Created = "created";
    public const string Name = "name";
    public const string Downloads = "downloads";
}
