namespace CloudLauncher.Shared;

/// <summary>What a member is allowed to do inside a team.</summary>
/// <remarks>Ownership still lives in <c>Team.OwnerId</c>; roles only delegate. <see cref="Owner"/> is
/// a role so a member list can render from one column, and the row carrying it is always the team's
/// actual owner.</remarks>
public enum TeamRole
{
    Member = 0,
    Admin = 1,
    Owner = 2
}

public sealed record CreateTeamRequest(string Name);

/// <summary>Rename an existing team. Validated like <see cref="CreateTeamRequest"/>.</summary>
public sealed record RenameTeamRequest(string Name);

/// <summary>Hand a team to one of its existing members.</summary>
/// <param name="UserId">The new owner. Must already be a member; the old owner stays on as one.</param>
public sealed record TransferTeamOwnershipRequest(Guid UserId);

/// <param name="SharedPackCount">Packs shared with this team.</param>
/// <param name="SharedModCount">Hosted mods shared with this team.</param>
/// <param name="SharedWorldCount">Shared worlds shared with this team.</param>
/// <param name="SharedResourcePackCount">Hosted resource packs shared with this team.</param>
public sealed record TeamSummary(
    Guid Id,
    string Name,
    Guid OwnerId,
    string OwnerUsername,
    int MemberCount,
    int SharedPackCount,
    int SharedModCount,
    int SharedWorldCount,
    int SharedResourcePackCount,
    // Appended last with defaults so every existing positional construction site keeps compiling.
    int SharedBundleCount = 0,
    TeamRole MyRole = TeamRole.Member);

/// <param name="Members">Kept for older clients. <paramref name="MemberDetails"/> has the same people
/// plus the join date and owner flag.</param>
/// <param name="MemberDetails">The member list a UI should render.</param>
public sealed record TeamDetail(
    Guid Id,
    string Name,
    Guid OwnerId,
    string OwnerUsername,
    IReadOnlyList<UserSummary> Members,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TeamMemberEntry> MemberDetails,
    int SharedPackCount,
    int SharedModCount,
    int SharedWorldCount,
    int SharedResourcePackCount,
    int SharedBundleCount = 0,
    TeamRole MyRole = TeamRole.Member,
    IReadOnlyList<TeamInvitationEntry>? PendingInvitations = null);

/// <summary>One row of a team's member list.</summary>
/// <param name="IsOwner">Marks the owner, so the UI doesn't have to compare every row against
/// <see cref="TeamDetail.OwnerId"/>.</param>
public sealed record TeamMemberEntry(
    Guid UserId,
    string Username,
    bool EmailConfirmed,
    DateTimeOffset JoinedAt,
    bool IsOwner,
    TeamRole Role = TeamRole.Member);

public sealed record AddTeamMemberRequest(string Username);

/// <summary>Change what an existing member may do. The owner can't be demoted here; use
/// <see cref="TransferTeamOwnershipRequest"/>.</summary>
public sealed record UpdateTeamMemberRoleRequest(TeamRole Role);

// ── invitations ──────────────────────────────────────────────────────────────

/// <summary>One pending, accepted or revoked invitation to a team.</summary>
/// <param name="InvitedUserId">Null until a link invitation is redeemed.</param>
/// <param name="Token">Only ever returned to a caller who may manage the team's members.</param>
public sealed record TeamInvitationEntry(
    Guid Id,
    Guid TeamId,
    string TeamName,
    Guid? InvitedUserId,
    string? InvitedUsername,
    Guid InvitedByUserId,
    string InvitedByUsername,
    TeamRole Role,
    string? Message,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? RevokedAt,
    bool IsLink,
    string? Token = null);

/// <summary>Invite somebody to a team, or mint a join link for it.</summary>
/// <param name="Username">Who to invite. Null mints a link anybody holding it may redeem.</param>
public sealed record CreateTeamInvitationRequest(
    string? Username,
    TeamRole Role = TeamRole.Member,
    string? Message = null,
    int? ExpiresInDays = null);

/// <summary>Everything waiting for the signed-in user's answer, in one round trip.</summary>
/// <remarks>One request, so the notification badge doesn't flicker waiting on the slower of two
/// endpoints.</remarks>
public sealed record MyInvitations(
    IReadOnlyList<PackInvitationEntry> Packs,
    IReadOnlyList<TeamInvitationEntry> Teams,
    IReadOnlyList<BundleInvitationEntry> Bundles);

// ── what a team holds ────────────────────────────────────────────────────────

/// <summary>Which family a thing shared with a team belongs to.</summary>
/// <remarks>Separate from <c>ActivitySubjectType</c>, which has a <c>Team</c> member that can never
/// appear here.</remarks>
public enum TeamSharedKind
{
    Pack = 0,
    Mod = 1,
    World = 2,
    ResourcePack = 3,
    Bundle = 4
}

/// <summary>One thing a team has been granted access to, with everything a list row needs.</summary>
/// <param name="Slug">Null for packs, which have no slug.</param>
/// <param name="IconBlobHash">Null for packs, which have no icon.</param>
/// <param name="Permissions">What the grant gives every member of the team.</param>
/// <param name="Bundle">Which kind of bundle this is, when <paramref name="Kind"/> is
/// <see cref="TeamSharedKind.Bundle"/>; null otherwise.</param>
/// <param name="CanRevoke">Whether the caller may drop this grant through the family's own
/// <c>DELETE /{family}/{id}/teams/{teamId}</c> route. Currently that means owning the shared thing;
/// owning the team isn't enough, since those routes check the resource's owner.</param>
public sealed record TeamSharedItem(
    TeamSharedKind Kind,
    Guid Id,
    string Name,
    string? Slug,
    string? Summary,
    Guid OwnerId,
    string OwnerUsername,
    string? IconBlobHash,
    PackPermissions Permissions,
    DateTimeOffset UpdatedAt,
    BundleKind? Bundle = null,
    bool CanRevoke = false);

/// <summary>Everything shared with one team, newest first.</summary>
public sealed record TeamSharedContent(
    Guid TeamId,
    string TeamName,
    TeamRole MyRole,
    IReadOnlyList<TeamSharedItem> Items);

/// <summary>One pending invitation to a content bundle. Same shape as
/// <see cref="PackInvitationEntry"/>, and uses the same <c>PackInvitations</c> permission
/// model.</summary>
public sealed record BundleInvitationEntry(
    Guid Id,
    Guid BundleId,
    string BundleName,
    BundleKind Kind,
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
