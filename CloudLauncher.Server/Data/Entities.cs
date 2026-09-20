using Microsoft.AspNetCore.Identity;
using CloudLauncher.Shared;

namespace CloudLauncher.Server.Data;

public class AppUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When true the user gets the "admin" JWT role claim and can access /admin/* endpoints.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>Max bytes of shared-pack blobs this user may own. null = unlimited.</summary>
    public long? StorageQuotaBytes { get; set; } = 1L * 1024 * 1024 * 1024; // 1 GB default
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
/// <remarks>
/// Being able to open a pack and wanting it in your library are two different things. Access comes
/// from ownership, a collaborator grant or a team; this table is the user's own decision to add it.
/// Without the distinction, the moment someone shared a pack with you it appeared in your instances
/// unasked — and removing it meant deleting the collaborator row, i.e. throwing away the access the
/// owner had given you.
/// </remarks>
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
/// edits via the dev menu: mod-platform API keys (used by the server-side proxy
/// — never returned to clients) and the global default file-rules.</summary>
public class GlobalSetting
{
    public int Id { get; set; } = 1;
    public string? CurseForgeApiKey { get; set; }
    public string? ModrinthToken { get; set; }
    /// <summary>JSON-serialized List&lt;PackFileRule&gt;. Null = no admin override yet.</summary>
    public string? DefaultRulesJson { get; set; }
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

    /// <summary>Comma-separated list of MC versions the latest version supports, denormalized for fast search.</summary>
    public string? McVersionsCsv { get; set; }
    /// <summary>Comma-separated loader names (fabric,forge,…), denormalized for fast search.</summary>
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
