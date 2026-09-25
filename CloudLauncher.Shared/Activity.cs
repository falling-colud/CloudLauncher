namespace CloudLauncher.Shared;

/// <summary>What happened.</summary>
/// <remarks>Kept coarse: the feed answers who shared what with whom, and when. It is not an audit
/// trail, so reading, browsing and downloading are not recorded.</remarks>
public enum ActivityKind
{
    Shared = 0,
    Unshared = 1,
    PermissionsChanged = 2,
    VisibilityChanged = 3,
    Uploaded = 4,
    VersionPublished = 5,
    MemberAdded = 6,
    MemberRemoved = 7,
    RoleChanged = 8,
    InviteSent = 9,
    InviteAccepted = 10,
    OwnershipTransferred = 11
}

/// <summary>Which family the thing an entry is about belongs to.</summary>
public enum ActivitySubjectType
{
    Pack = 0,
    Mod = 1,
    World = 2,
    ResourcePack = 3,
    Bundle = 4,
    Team = 5
}

/// <summary>One line of an activity feed.</summary>
/// <param name="SubjectName">The subject's name when the entry was written. Denormalised so a
/// deleted pack still shows by name rather than as a blank.</param>
/// <param name="ActorUsername">Null when the actor's account is gone.</param>
/// <param name="Detail">A short free-text addition (the permissions granted, a member's new role,
/// the version published). Never a sentence to show on its own.</param>
public sealed record ActivityFeedEntry(
    Guid Id,
    ActivityKind Kind,
    ActivitySubjectType SubjectType,
    Guid SubjectId,
    string SubjectName,
    Guid ActorUserId,
    string? ActorUsername,
    Guid? TargetUserId,
    string? TargetUsername,
    Guid? TargetTeamId,
    string? TargetTeamName,
    string? Detail,
    DateTimeOffset CreatedAt);

public sealed record ActivityFeedPage(
    IReadOnlyList<ActivityFeedEntry> Items,
    int Offset,
    int Limit,
    int Total);
