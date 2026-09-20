namespace CloudLauncher.Shared;

public sealed record CreateTeamRequest(string Name);

/// <summary>Rename an existing team. Validated exactly as <see cref="CreateTeamRequest"/> is.</summary>
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
    int SharedResourcePackCount);

/// <param name="Members">Kept for older clients. <paramref name="MemberDetails"/> carries the same
/// people with the join date and owner flag the member list actually needs to be readable.</param>
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
    int SharedResourcePackCount);

/// <summary>One row of a team's member list.</summary>
/// <param name="IsOwner">The owner is a member like any other; only this flag distinguishes them,
/// so the UI does not have to compare every row against <see cref="TeamDetail.OwnerId"/>.</param>
public sealed record TeamMemberEntry(
    Guid UserId,
    string Username,
    bool EmailConfirmed,
    DateTimeOffset JoinedAt,
    bool IsOwner);

public sealed record AddTeamMemberRequest(string Username);
