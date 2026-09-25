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
    string? Summary = null,
    // Appended last with defaults so existing positional constructions (including the launcher's
    // offline scanner) keep compiling and meaning the same.
    Guid? LastUploadedById = null,
    string? LastUploadedByUsername = null,
    DateTimeOffset? LastUploadedAt = null);

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
    string? Summary = null,
    Guid? LastUploadedById = null,
    string? LastUploadedByUsername = null,
    DateTimeOffset? LastUploadedAt = null,
    // The pack's current share-link token, or null when none has been minted. Only filled in for
    // callers who may manage collaborators, since the token grants access.
    string? ShareToken = null,
    IReadOnlyList<PackInvitationEntry>? PendingInvitations = null);

public sealed record PackCollaboratorEntry(
    Guid UserId,
    string Username,
    PackPermissions Permissions);

/// <summary>One team a pack is shared with, with enough of its roster to render "who has
/// access".</summary>
/// <param name="MemberCount">How many people are on the team. Zero on rows from an older server.</param>
/// <param name="MemberUsernames">The first few member names, oldest membership first: a preview for
/// a line like "Friends (5): name, name, ...", not the full roster. Null when the server did not
/// send one.</param>
/// <remarks>Appended last with defaults so existing positional constructions keep compiling.
/// Included because <c>GET /teams/{id}</c> only answers for team members, so an owner outside the
/// team couldn't otherwise see who they are sharing with.</remarks>
public sealed record PackTeamEntry(
    Guid TeamId,
    string TeamName,
    PackPermissions Permissions,
    int MemberCount = 0,
    IReadOnlyList<string>? MemberUsernames = null);

/// <summary>Hand a pack to one of its existing collaborators.</summary>
/// <param name="UserId">The new owner. Must already be a collaborator; the old owner stays on as
/// one with full permissions, as with a team transfer.</param>
public sealed record TransferPackOwnershipRequest(Guid UserId);

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

// ── invitations ──────────────────────────────────────────────────────────────

/// <summary>One pending, accepted or revoked invitation to a pack.</summary>
/// <param name="InvitedUserId">Null until a link invitation is redeemed, since a link has no
/// target user.</param>
/// <param name="IsLink">True when the invitation was minted as a share link rather than addressed
/// to somebody by name.</param>
/// <param name="Token">The redeem token. Only ever returned to a caller who may manage the pack's
/// collaborators; for everyone else this is null.</param>
public sealed record PackInvitationEntry(
    Guid Id,
    Guid PackId,
    string PackName,
    Guid? InvitedUserId,
    string? InvitedUsername,
    Guid InvitedByUserId,
    string InvitedByUsername,
    PackPermissions Permissions,
    string? Message,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? RevokedAt,
    bool IsLink,
    string? Token = null);

/// <summary>Invite somebody to a pack, or mint a share link for it.</summary>
/// <param name="Username">Who to invite. Null mints a link anybody holding it may redeem.</param>
/// <param name="ExpiresInDays">Null leaves the invitation open indefinitely.</param>
public sealed record CreatePackInvitationRequest(
    string? Username,
    PackPermissions Permissions,
    string? Message = null,
    int? ExpiresInDays = null);

/// <summary>What a redeemer is shown before they decide: the name, who is inviting them and what
/// they would be granted, but nothing about the pack's contents.</summary>
public sealed record InvitationPreview(
    Guid Id,
    string SubjectName,
    string InvitedByUsername,
    PackPermissions Permissions,
    string? Message,
    DateTimeOffset? ExpiresAt,
    bool AlreadyAccepted,
    bool Revoked,
    bool Expired);

public sealed record RedeemInvitationRequest(string Token);

/// <summary>The active share link for a pack or bundle, or null when none has been minted.</summary>
public sealed record ShareLinkInfo(
    string Token,
    PackPermissions Permissions,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>Mint (or re-mint) a share link. Re-minting revokes the previous one.</summary>
public sealed record CreateShareLinkRequest(
    PackPermissions Permissions,
    int? ExpiresInDays = null);
