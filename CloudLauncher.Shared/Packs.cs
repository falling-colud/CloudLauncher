namespace CloudLauncher.Shared;

public enum PackVisibility
{
    Private = 0,
    Team = 1,
    Public = 2
}

public enum LoaderKind
{
    None = 0,
    Fabric = 1,
    Forge = 2,
    NeoForge = 3
}

[Flags]
public enum PackPermissions
{
    None = 0,
    View = 1 << 0,
    Download = 1 << 1,
    UploadShared = 1 << 2,
    ManageCollaborators = 1 << 3,

    ReadOnly = View | Download,
    Contributor = View | Download | UploadShared,
    Full = View | Download | UploadShared | ManageCollaborators
}

public sealed record CreatePackRequest(
    string Name,
    string? Description,
    bool IsEmpty,
    string? MinecraftVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    string? Summary = null);

public sealed record UpdatePackRequest(
    string? Name,
    string? Description,
    PackVisibility? Visibility,
    bool? IsShared,
    bool? IsEmpty,
    string? MinecraftVersion,
    LoaderKind? Loader,
    string? LoaderVersion,
    // Pass null to leave rules unchanged; pass empty list to clear them.
    List<PackFileRule>? Rules = null,
    string? Summary = null);

/// <summary>A file-routing rule stored server-side so all collaborators share the same rules.</summary>
public sealed record PackFileRule(string Pattern, PackRuleAction Action);

public enum PackRuleAction
{
    Local   = 0,
    Shared  = 1,
    Ignored = 2
}

public sealed record PackSummary(
    Guid Id,
    string Name,
    string? Description,
    Guid OwnerId,
    string OwnerUsername,
    PackVisibility Visibility,
    bool IsShared,
    bool IsEmpty,
    string? MinecraftVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PackPermissions EffectivePermissions,
    string? Summary = null);

public sealed record PackDetail(
    Guid Id,
    string Name,
    string? Description,
    Guid OwnerId,
    string OwnerUsername,
    PackVisibility Visibility,
    bool IsShared,
    bool IsEmpty,
    string? MinecraftVersion,
    LoaderKind Loader,
    string? LoaderVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    PackPermissions EffectivePermissions,
    IReadOnlyList<PackCollaboratorEntry> Collaborators,
    IReadOnlyList<PackTeamEntry> Teams,
    IReadOnlyList<PackFileRule> Rules,
    string? Summary = null);

public sealed record PackCollaboratorEntry(
    Guid UserId,
    string Username,
    PackPermissions Permissions);

public sealed record PackTeamEntry(
    Guid TeamId,
    string TeamName,
    PackPermissions Permissions);

public sealed record AddCollaboratorRequest(string Username, PackPermissions Permissions);
public sealed record UpdateCollaboratorRequest(PackPermissions Permissions);

public sealed record AddPackTeamRequest(Guid TeamId, PackPermissions Permissions);
public sealed record UpdatePackTeamRequest(PackPermissions Permissions);

public enum PackBrowseSource
{
    Public = 0,
    Team = 1,
    Shared = 2,
    Owned = 3
}

public sealed record PackBrowsePage(
    IReadOnlyList<PackSummary> Items,
    int Offset,
    int Limit,
    int Total);
