using Microsoft.AspNetCore.Identity;
using CloudLauncher.Shared;

namespace CloudLauncher.Server.Data;

public class AppUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When true the user gets the "admin" JWT role claim and can access /admin/*
    /// endpoints.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>Max bytes of shared-pack blobs this user may own. null = unlimited.</summary>
    public long? StorageQuotaBytes { get; set; } = 1L * 1024 * 1024 * 1024; // 1 GB default

    /// <summary>When the user accepted the Terms and Privacy Policy at sign-up. Null for accounts
    /// made before the terms screen existed, or by a launcher that did not show it.</summary>
    public DateTimeOffset? TermsAcceptedAt { get; set; }

    /// <summary>The <c>Legal.TermsVersion</c> they accepted.</summary>
    public string? TermsVersion { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string TokenHash { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>The token a refresh swapped this one for. Set only by rotation, never by a sign-out,
    /// so a rotated token coming back can be told apart from one the user gave up.</summary>
    public Guid? ReplacedById { get; set; }
}

public class Pack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
    public string? Summary { get; set; }
    public string? Description { get; set; }

    public Guid OwnerId { get; set; }
    public AppUser Owner { get; set; } = null!;

    public PackVisibility Visibility { get; set; } = PackVisibility.Private;
    public bool IsShared { get; set; }

    public bool IsEmpty { get; set; } = true;
    public string? MinecraftVersion { get; set; }
    public LoaderKind Loader { get; set; } = LoaderKind.None;
    public string? LoaderVersion { get; set; }

    /// <summary>Pack-specific file rules, serialized as JSON. Null = no rules saved yet.</summary>
    public string? RulesJson { get; set; }

    public long ManifestVersion { get; set; } = 0;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The token of this pack's currently-active share link, or null when none is minted.</summary>
    /// <remarks>A denormalised pointer, not a grant: the link's permissions live on the
    /// <see cref="PackInvitation"/> row with the same token, so redeeming a link goes through the same
    /// checks as a named invitation. Stored here only to save a join.</remarks>
    public string? ShareToken { get; set; }
    public DateTimeOffset? ShareTokenCreatedAt { get; set; }

    /// <summary>Who last pushed files to this pack, and when.</summary>
    /// <remarks><see cref="UpdatedAt"/> changes on every PATCH, so it can't answer "last upload".
    /// Nullable for packs predating this column and for uploaders who have deleted their account.</remarks>
    public Guid? LastUploadedById { get; set; }
    public AppUser? LastUploadedBy { get; set; }
    public DateTimeOffset? LastUploadedAt { get; set; }

    public List<PackCollaborator> Collaborators { get; set; } = new();
    public List<PackTeam> Teams { get; set; } = new();
    public List<PackManifestEntry> ManifestEntries { get; set; } = new();
}

public class PackCollaborator
{
    public Guid PackId { get; set; }
    public Pack Pack { get; set; } = null!;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public PackPermissions Permissions { get; set; }
}

/// <summary>
/// A pack a user has chosen to keep in their instance list.
/// </summary>
/// <remarks>Access comes from ownership, a collaborator grant or a team; this row is the user's own
/// choice to list it. Keeping them separate means shared packs don't appear unasked, and removing
/// one from the list doesn't throw away the access.</remarks>
public class PackListing
{
    public Guid PackId { get; set; }
    public Pack Pack { get; set; } = null!;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Team
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
    public Guid OwnerId { get; set; }
    public AppUser Owner { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TeamMember> Members { get; set; } = new();
}

public class TeamMember
{
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>What this member may do beyond being on the team.</summary>
    /// <remarks><see cref="Team.OwnerId"/> remains the source of truth for ownership; this is delegation
    /// on top. The owner's row carries <see cref="TeamRole.Owner"/> so a member list renders from one
    /// column.</remarks>
    public TeamRole Role { get; set; } = TeamRole.Member;
}

public class PackTeam
{
    public Guid PackId { get; set; }
    public Pack Pack { get; set; } = null!;
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public PackPermissions Permissions { get; set; }
}

public class PackManifestEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PackId { get; set; }
    public Pack Pack { get; set; } = null!;
    public string RelativePath { get; set; } = null!;
    public string Hash { get; set; } = null!;
    public long Size { get; set; }
}

/// <summary>Singleton row (Id = 1) holding the launcher-wide settings the admin
/// edits via the dev menu: mod-platform API keys (used by the server-side proxy,
/// never returned to clients) and the global default file-rules.</summary>
public class GlobalSetting
{
    public int Id { get; set; } = 1;
    public string? CurseForgeApiKey { get; set; }
    public string? ModrinthToken { get; set; }
    /// <summary>JSON-serialized List&lt;PackFileRule&gt;. Null = no admin override yet.</summary>
    public string? DefaultRulesJson { get; set; }
    /// <summary>True while an admin has closed sign-ups. Stored as "closed" so the default,
    /// and a missing row, both mean open.</summary>
    public bool RegistrationClosed { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ── server-hosted mods ───────────────────────────────────────────────────────

public class Mod
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Summary { get; set; }
    public string? Description { get; set; }

    public Guid OwnerId { get; set; }
    public AppUser Owner { get; set; } = null!;

    public PackVisibility Visibility { get; set; } = PackVisibility.Private;

    /// <summary>Comma-separated MC versions the latest version supports, denormalized for fast
    /// search.</summary>
    public string? McVersionsCsv { get; set; }
    /// <summary>Comma-separated loader names (fabric,forge,...), denormalized for fast search.</summary>
    public string? LoadersCsv { get; set; }

    public string? IconBlobHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ModVersion> Versions { get; set; } = new();
    public List<ModCollaborator> Collaborators { get; set; } = new();
    public List<ModTeam> Teams { get; set; } = new();
}

public class ModVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ModId { get; set; }
    public Mod Mod { get; set; } = null!;

    public string VersionString { get; set; } = null!;
    public string? Changelog { get; set; }
    public string ReleaseChannel { get; set; } = "release"; // release / beta / alpha

    public string BlobHash { get; set; } = null!;
    public long FileSize { get; set; }
    public string FileName { get; set; } = null!;

    public string? McVersionsCsv { get; set; }
    public string? LoadersCsv { get; set; }

    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ModCollaborator
{
    public Guid ModId { get; set; }
    public Mod Mod { get; set; } = null!;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public PackPermissions Permissions { get; set; }
}

public class ModTeam
{
    public Guid ModId { get; set; }
    public Mod Mod { get; set; } = null!;
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public PackPermissions Permissions { get; set; }
}

// ── server-hosted worlds ─────────────────────────────────────────────────────

public class SharedWorld
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Summary { get; set; }
    public string? Description { get; set; }

    public Guid OwnerId { get; set; }
    public AppUser Owner { get; set; } = null!;

    public PackVisibility Visibility { get; set; } = PackVisibility.Private;

    /// <summary>MC version the world was last saved with, denormalized for browse filters.</summary>
    public string? McVersion { get; set; }

    public string? IconBlobHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<SharedWorldVersion> Versions { get; set; } = new();
    public List<SharedWorldCollaborator> Collaborators { get; set; } = new();
    public List<SharedWorldTeam> Teams { get; set; } = new();
}

public class SharedWorldVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorldId { get; set; }
    public SharedWorld World { get; set; } = null!;

    public string VersionString { get; set; } = null!;
    public string? Changelog { get; set; }

    public string BlobHash { get; set; } = null!;
    public long FileSize { get; set; }
    public string FileName { get; set; } = null!;

    public string? McVersion { get; set; }

    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SharedWorldCollaborator
{
    public Guid WorldId { get; set; }
    public SharedWorld World { get; set; } = null!;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public PackPermissions Permissions { get; set; }
}

public class SharedWorldTeam
{
    public Guid WorldId { get; set; }
    public SharedWorld World { get; set; } = null!;
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public PackPermissions Permissions { get; set; }
}

// ── server-hosted resource packs ─────────────────────────────────────────────

public class HostedResourcePack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Summary { get; set; }
    public string? Description { get; set; }

    public Guid OwnerId { get; set; }
    public AppUser Owner { get; set; } = null!;

    public PackVisibility Visibility { get; set; } = PackVisibility.Private;

    public string? McVersionsCsv { get; set; }

    public string? IconBlobHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<HostedResourcePackVersion> Versions { get; set; } = new();
    public List<HostedResourcePackCollaborator> Collaborators { get; set; } = new();
    public List<HostedResourcePackTeam> Teams { get; set; } = new();
}

public class HostedResourcePackVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResourcePackId { get; set; }
    public HostedResourcePack ResourcePack { get; set; } = null!;

    public string VersionString { get; set; } = null!;
    public string? Changelog { get; set; }
    public string ReleaseChannel { get; set; } = "release";

    public string BlobHash { get; set; } = null!;
    public long FileSize { get; set; }
    public string FileName { get; set; } = null!;

    public string? McVersionsCsv { get; set; }

    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class HostedResourcePackCollaborator
{
    public Guid ResourcePackId { get; set; }
    public HostedResourcePack ResourcePack { get; set; } = null!;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public PackPermissions Permissions { get; set; }
}

public class HostedResourcePackTeam
{
    public Guid ResourcePackId { get; set; }
    public HostedResourcePack ResourcePack { get; set; } = null!;
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public PackPermissions Permissions { get; set; }
}

// ── server-hosted content bundles ────────────────────────────────────────────

/// <summary>A versioned archive of instance files: shaders, config, kubejs, datapacks.</summary>
/// <remarks>One family for every shareable folder kind after mods, worlds and resource packs, told
/// apart by <see cref="Kind"/> and <see cref="TargetPathRoot"/> (the folder it unpacks into).
/// Shaped like <see cref="HostedResourcePack"/>, column for column.</remarks>
public class ContentBundle
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public BundleKind Kind { get; set; } = BundleKind.Other;

    /// <summary>Url-safe name, unique per <see cref="Kind"/> rather than globally; the kind is part of
    /// the route, so a shader pack and a config bundle can both be "vanilla".</summary>
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Summary { get; set; }
    public string? Description { get; set; }

    public Guid OwnerId { get; set; }
    public AppUser Owner { get; set; } = null!;

    public PackVisibility Visibility { get; set; } = PackVisibility.Private;

    /// <summary>Relative folder inside an instance this bundle unpacks into ("shaderpacks",
    /// "config", "kubejs", "datapacks"). Empty means the instance root.</summary>
    /// <remarks>Stored per bundle rather than derived from <see cref="Kind"/>, so a bundle can target
    /// another folder without a new enum member and migration.</remarks>
    public string TargetPathRoot { get; set; } = "";

    /// <summary>Comma-separated list of MC versions, denormalized for fast search.</summary>
    public string? McVersionsCsv { get; set; }
    /// <summary>Comma-separated loader names (fabric,forge,...), denormalized for fast search.</summary>
    public string? LoadersCsv { get; set; }

    public string? IconBlobHash { get; set; }

    /// <summary>How many times any version of this bundle has been downloaded. The older families
    /// report a hard-coded zero instead.</summary>
    public long DownloadCount { get; set; }

    /// <summary>The token of this bundle's currently-active share link: a pointer to a
    /// <see cref="ContentBundleInvitation"/>, not a grant (see <see cref="Pack.ShareToken"/>).</summary>
    public string? ShareToken { get; set; }
    public DateTimeOffset? ShareTokenCreatedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ContentBundleVersion> Versions { get; set; } = new();
    public List<ContentBundleCollaborator> Collaborators { get; set; } = new();
    public List<ContentBundleTeam> Teams { get; set; } = new();
}

public class ContentBundleVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BundleId { get; set; }
    public ContentBundle Bundle { get; set; } = null!;

    public string VersionString { get; set; } = null!;
    public string? Changelog { get; set; }
    public string ReleaseChannel { get; set; } = "release"; // release / beta / alpha

    public string BlobHash { get; set; } = null!;
    public long FileSize { get; set; }
    public string FileName { get; set; } = null!;

    public string? McVersionsCsv { get; set; }
    public string? LoadersCsv { get; set; }

    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ContentBundleCollaborator
{
    public Guid BundleId { get; set; }
    public ContentBundle Bundle { get; set; } = null!;
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public PackPermissions Permissions { get; set; }
}

public class ContentBundleTeam
{
    public Guid BundleId { get; set; }
    public ContentBundle Bundle { get; set; } = null!;
    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;
    public PackPermissions Permissions { get; set; }
}

// ── invitations ──────────────────────────────────────────────────────────────

/// <summary>An offer of access to a pack, waiting for the invitee to accept it.</summary>
/// <remarks>Access is granted on accept, not on offer. <see cref="InvitedUserId"/> is null for a
/// share link until someone redeems it; a named invitation fills it and
/// <see cref="InvitedUsername"/>, which keeps the list readable after the account is deleted.</remarks>
public class PackInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PackId { get; set; }
    public Pack Pack { get; set; } = null!;

    /// <summary>Who this is for. Null while a link invitation is unredeemed.</summary>
    public Guid? InvitedUserId { get; set; }
    public AppUser? InvitedUser { get; set; }
    /// <summary>The username as it was typed, kept so a list still reads sensibly once the account
    /// it pointed at is deleted.</summary>
    public string? InvitedUsername { get; set; }

    public Guid InvitedByUserId { get; set; }
    public AppUser InvitedBy { get; set; } = null!;

    /// <summary>What accepting grants. The only place a share link's permissions are recorded.</summary>
    public PackPermissions Permissions { get; set; }

    /// <summary>Url-safe redeem token, unique across the table.</summary>
    public string Token { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>A line from the inviter, shown on the accept screen.</summary>
    public string? Message { get; set; }
}

/// <summary>An offer of membership in a team. The team-shaped twin of <see cref="PackInvitation"/>;
/// see that type for why the invited user is nullable.</summary>
public class TeamInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TeamId { get; set; }
    public Team Team { get; set; } = null!;

    public Guid? InvitedUserId { get; set; }
    public AppUser? InvitedUser { get; set; }
    public string? InvitedUsername { get; set; }

    public Guid InvitedByUserId { get; set; }
    public AppUser InvitedBy { get; set; } = null!;

    /// <summary>The role the invitee joins with.</summary>
    public TeamRole Role { get; set; } = TeamRole.Member;

    public string Token { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public string? Message { get; set; }
}

/// <summary>An offer of access to a content bundle.</summary>
/// <remarks>A separate table rather than a nullable subject column on <see cref="PackInvitation"/>,
/// so a bundle link resolves to a row with its permissions and a real foreign key deletes the
/// invitations with the bundle.</remarks>
public class ContentBundleInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BundleId { get; set; }
    public ContentBundle Bundle { get; set; } = null!;

    public Guid? InvitedUserId { get; set; }
    public AppUser? InvitedUser { get; set; }
    public string? InvitedUsername { get; set; }

    public Guid InvitedByUserId { get; set; }
    public AppUser InvitedBy { get; set; } = null!;

    public PackPermissions Permissions { get; set; }

    public string Token { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public string? Message { get; set; }
}

// ── activity ─────────────────────────────────────────────────────────────────

/// <summary>One line of the activity feed: who did what to which thing, and when.</summary>
/// <remarks>
/// <para>Append-only and small: it feeds the line under a pack ("X shared Sable with Friends, 3 days
/// ago"), not an audit trail, so reads and downloads aren't logged and entries are never
/// updated.</para>
/// <para><see cref="SubjectId"/> has no foreign key so a line still renders after the subject is
/// deleted; <see cref="SubjectName"/> is copied at write time for the same reason.</para>
/// </remarks>
public class ActivityEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ActorUserId { get; set; }
    public AppUser Actor { get; set; } = null!;

    public ActivityKind Kind { get; set; }

    public ActivitySubjectType SubjectType { get; set; }
    /// <summary>Id of the pack, mod, world, resource pack, bundle or team. Not a foreign key; see
    /// <see cref="ActivityEntry"/>.</summary>
    public Guid SubjectId { get; set; }
    /// <summary>The subject's name as it read when this was written.</summary>
    public string SubjectName { get; set; } = null!;

    /// <summary>The person this was done to, if any: the invitee, the member added.</summary>
    public Guid? TargetUserId { get; set; }
    public AppUser? TargetUser { get; set; }

    /// <summary>The team this was done to or with, when there is one.</summary>
    public Guid? TargetTeamId { get; set; }
    public Team? TargetTeam { get; set; }

    /// <summary>A short rider: the permission set granted, the role moved to, the version published.
    /// Never a sentence meant to stand on its own.</summary>
    public string? Detail { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
