namespace CloudLauncher.Shared;

public sealed record CreateTeamRequest(string Name);

public sealed record TeamSummary(
    Guid Id,
    string Name,
    Guid OwnerId,
    string OwnerUsername,
    int MemberCount);

public sealed record TeamDetail(
    Guid Id,
    string Name,
    Guid OwnerId,
    string OwnerUsername,
    IReadOnlyList<UserSummary> Members);

public sealed record AddTeamMemberRequest(string Username);
