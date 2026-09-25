using System.Text.Json;
using System.Text.Json.Serialization;
using CloudLauncher.Server.Auth;
using CloudLauncher.Shared;
using Microsoft.EntityFrameworkCore;

namespace CloudLauncher.Server.Data;

/// <summary>Everything the server holds about one account, as a JSON document the user can download.</summary>
/// <remarks>
/// Descriptions of files, not the files: names, sizes and content hashes, never the blobs. Never
/// password hashes, security stamps, refresh tokens or invitation tokens either.
/// </remarks>
public static class AccountExport
{
    public const string FileName = "cloudlauncher-account-export.json";

    /// <summary>The newest activity lines included. The feed only grows.</summary>
    private const int MaxActivity = 5000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<byte[]> BuildAsync(AppDbContext db, AppUser user, CancellationToken ct = default)
    {
        var id = user.Id;

        var profile = new Profile(
            user.Id, user.UserName ?? "", user.Email, user.EmailConfirmed, user.CreatedAt, user.IsAdmin,
            user.TermsAcceptedAt, user.TermsVersion,
            GoogleLinked: await db.UserLogins.AnyAsync(
                l => l.UserId == id && l.LoginProvider == GoogleAuthService.Provider, ct),
            HasPassword: user.PasswordHash is not null,
            user.StorageQuotaBytes);

        var activity = await db.ActivityEntries.AsNoTracking()
            .Where(e => e.ActorUserId == id || e.TargetUserId == id)
            .OrderByDescending(e => e.CreatedAt)
            .Take(MaxActivity + 1)
            .Select(e => new ActivityLine(
                e.Id, e.CreatedAt, e.Kind, e.SubjectType, e.SubjectId, e.SubjectName,
                e.Actor.UserName,
                e.TargetUser != null ? e.TargetUser.UserName : null,
                e.TargetTeam != null ? e.TargetTeam.Name : null,
                e.Detail))
            .ToListAsync(ct);
        var activityTruncated = activity.Count > MaxActivity;
        if (activityTruncated) activity.RemoveAt(activity.Count - 1);

        var document = new Document(
            Format: 1,
            ExportedAt: DateTimeOffset.UtcNow,
            Profile: profile,
            Packs: await PacksAsync(db, id, ct),
            HostedItems: await HostedItemsAsync(db, id, ct),
            Teams: await TeamsAsync(db, id, ct),
            AccessToOthersItems: await AccessAsync(db, id, ct),
            Library: await LibraryAsync(db, id, ct),
            InvitationsSent: await InvitationsAsync(db, id, sent: true, ct),
            InvitationsReceived: await InvitationsAsync(db, id, sent: false, ct),
            Activity: activity,
            ActivityTruncated: activityTruncated);

        return JsonSerializer.SerializeToUtf8Bytes(document, Json);
    }

    private static async Task<List<OwnedPack>> PacksAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var packs = await db.Packs.AsNoTracking()
            .Where(p => p.OwnerId == id)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
        var files = (await db.PackManifestEntries.AsNoTracking()
                .Where(e => e.Pack.OwnerId == id)
                .Select(e => new { e.PackId, e.RelativePath, e.Hash, e.Size })
                .ToListAsync(ct))
            .GroupBy(e => e.PackId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(e => e.RelativePath, StringComparer.Ordinal)
                      .Select(e => new PackFile(e.RelativePath, e.Hash, e.Size))
                      .ToList());

        return packs.Select(p => new OwnedPack(
            p.Id, p.Name, p.Summary, p.Description, p.Visibility, p.IsShared, p.IsEmpty,
            p.MinecraftVersion, p.Loader, p.LoaderVersion, p.RulesJson, p.CreatedAt, p.UpdatedAt,
            files.GetValueOrDefault(p.Id) ?? new List<PackFile>())).ToList();
    }

    private static async Task<List<HostedItem>> HostedItemsAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var items = new List<HostedItem>();

        items.AddRange(await db.Mods.AsNoTracking()
            .Where(m => m.OwnerId == id)
            .OrderBy(m => m.CreatedAt)
            .Select(m => new HostedItem(
                "mod", m.Id, m.Slug, m.Name, m.Summary, m.Description, m.Visibility,
                null, null, m.IconBlobHash, m.CreatedAt, m.UpdatedAt,
                m.Versions.OrderBy(v => v.PublishedAt).Select(v => new ItemVersion(
                    v.Id, v.VersionString, v.ReleaseChannel, v.Changelog,
                    v.FileName, v.FileSize, v.BlobHash, v.PublishedAt)).ToList()))
            .ToListAsync(ct));

        items.AddRange(await db.SharedWorlds.AsNoTracking()
            .Where(w => w.OwnerId == id)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new HostedItem(
                "world", w.Id, w.Slug, w.Name, w.Summary, w.Description, w.Visibility,
                null, null, w.IconBlobHash, w.CreatedAt, w.UpdatedAt,
                w.Versions.OrderBy(v => v.PublishedAt).Select(v => new ItemVersion(
                    v.Id, v.VersionString, null, v.Changelog,
                    v.FileName, v.FileSize, v.BlobHash, v.PublishedAt)).ToList()))
            .ToListAsync(ct));

        items.AddRange(await db.HostedResourcePacks.AsNoTracking()
            .Where(r => r.OwnerId == id)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new HostedItem(
                "resourcepack", r.Id, r.Slug, r.Name, r.Summary, r.Description, r.Visibility,
                null, null, r.IconBlobHash, r.CreatedAt, r.UpdatedAt,
                r.Versions.OrderBy(v => v.PublishedAt).Select(v => new ItemVersion(
                    v.Id, v.VersionString, v.ReleaseChannel, v.Changelog,
                    v.FileName, v.FileSize, v.BlobHash, v.PublishedAt)).ToList()))
            .ToListAsync(ct));

        items.AddRange(await db.ContentBundles.AsNoTracking()
            .Where(b => b.OwnerId == id)
            .OrderBy(b => b.CreatedAt)
            .Select(b => new HostedItem(
                "bundle", b.Id, b.Slug, b.Name, b.Summary, b.Description, b.Visibility,
                b.Kind, b.TargetPathRoot, b.IconBlobHash, b.CreatedAt, b.UpdatedAt,
                b.Versions.OrderBy(v => v.PublishedAt).Select(v => new ItemVersion(
                    v.Id, v.VersionString, v.ReleaseChannel, v.Changelog,
                    v.FileName, v.FileSize, v.BlobHash, v.PublishedAt)).ToList()))
            .ToListAsync(ct));

        return items;
    }

    private static async Task<List<TeamMembership>> TeamsAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var teams = await db.Teams.AsNoTracking()
            .Where(t => t.OwnerId == id || t.Members.Any(m => m.UserId == id))
            .OrderBy(t => t.CreatedAt)
            .Select(t => new TeamMembership(
                t.Id, t.Name, t.Owner.UserName ?? "",
                t.OwnerId == id
                    ? TeamRole.Owner
                    : t.Members.Where(m => m.UserId == id).Select(m => m.Role).FirstOrDefault(),
                t.Members.Where(m => m.UserId == id).Select(m => (DateTimeOffset?)m.JoinedAt).FirstOrDefault(),
                t.Members.Count))
            .ToListAsync(ct);
        return teams;
    }

    /// <summary>Other people's things this user can reach, directly or through a team.</summary>
    private static async Task<List<Access>> AccessAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        const string direct = "direct";
        var access = new List<Access>();

        access.AddRange(await db.PackCollaborators.AsNoTracking()
            .Where(c => c.UserId == id && c.Pack.OwnerId != id)
            .Select(c => new Access("pack", c.PackId, c.Pack.Name, c.Pack.Owner.UserName ?? "", c.Permissions, direct, null))
            .ToListAsync(ct));
        access.AddRange(await db.ModCollaborators.AsNoTracking()
            .Where(c => c.UserId == id && c.Mod.OwnerId != id)
            .Select(c => new Access("mod", c.ModId, c.Mod.Name, c.Mod.Owner.UserName ?? "", c.Permissions, direct, null))
            .ToListAsync(ct));
        access.AddRange(await db.SharedWorldCollaborators.AsNoTracking()
            .Where(c => c.UserId == id && c.World.OwnerId != id)
            .Select(c => new Access("world", c.WorldId, c.World.Name, c.World.Owner.UserName ?? "", c.Permissions, direct, null))
            .ToListAsync(ct));
        access.AddRange(await db.HostedResourcePackCollaborators.AsNoTracking()
            .Where(c => c.UserId == id && c.ResourcePack.OwnerId != id)
            .Select(c => new Access("resourcepack", c.ResourcePackId, c.ResourcePack.Name, c.ResourcePack.Owner.UserName ?? "", c.Permissions, direct, null))
            .ToListAsync(ct));
        access.AddRange(await db.ContentBundleCollaborators.AsNoTracking()
            .Where(c => c.UserId == id && c.Bundle.OwnerId != id)
            .Select(c => new Access("bundle", c.BundleId, c.Bundle.Name, c.Bundle.Owner.UserName ?? "", c.Permissions, direct, null))
            .ToListAsync(ct));

        access.AddRange(await db.PackTeams.AsNoTracking()
            .Where(g => g.Pack.OwnerId != id && g.Team.Members.Any(m => m.UserId == id))
            .Select(g => new Access("pack", g.PackId, g.Pack.Name, g.Pack.Owner.UserName ?? "", g.Permissions, "team", g.Team.Name))
            .ToListAsync(ct));
        access.AddRange(await db.ModTeams.AsNoTracking()
            .Where(g => g.Mod.OwnerId != id && g.Team.Members.Any(m => m.UserId == id))
            .Select(g => new Access("mod", g.ModId, g.Mod.Name, g.Mod.Owner.UserName ?? "", g.Permissions, "team", g.Team.Name))
            .ToListAsync(ct));
        access.AddRange(await db.SharedWorldTeams.AsNoTracking()
            .Where(g => g.World.OwnerId != id && g.Team.Members.Any(m => m.UserId == id))
            .Select(g => new Access("world", g.WorldId, g.World.Name, g.World.Owner.UserName ?? "", g.Permissions, "team", g.Team.Name))
            .ToListAsync(ct));
        access.AddRange(await db.HostedResourcePackTeams.AsNoTracking()
            .Where(g => g.ResourcePack.OwnerId != id && g.Team.Members.Any(m => m.UserId == id))
            .Select(g => new Access("resourcepack", g.ResourcePackId, g.ResourcePack.Name, g.ResourcePack.Owner.UserName ?? "", g.Permissions, "team", g.Team.Name))
            .ToListAsync(ct));
        access.AddRange(await db.ContentBundleTeams.AsNoTracking()
            .Where(g => g.Bundle.OwnerId != id && g.Team.Members.Any(m => m.UserId == id))
            .Select(g => new Access("bundle", g.BundleId, g.Bundle.Name, g.Bundle.Owner.UserName ?? "", g.Permissions, "team", g.Team.Name))
            .ToListAsync(ct));

        return access;
    }

    /// <summary>Other people's packs the user added to their instance list.</summary>
    private static Task<List<LibraryEntry>> LibraryAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        db.PackListings.AsNoTracking()
            .Where(l => l.UserId == id)
            .OrderBy(l => l.AddedAt)
            .Select(l => new LibraryEntry(l.PackId, l.Pack.Name, l.Pack.Owner.UserName ?? "", l.AddedAt))
            .ToListAsync(ct);

    /// <summary>Invitations the user sent, or ones addressed to them. Tokens are left out.</summary>
    private static async Task<List<Invitation>> InvitationsAsync(AppDbContext db, Guid id, bool sent, CancellationToken ct)
    {
        var invitations = new List<Invitation>();

        invitations.AddRange(await db.PackInvitations.AsNoTracking()
            .Where(i => sent ? i.InvitedByUserId == id : i.InvitedUserId == id)
            .Select(i => new Invitation(
                "pack", i.Id, i.PackId, i.Pack.Name,
                i.InvitedUser != null ? i.InvitedUser.UserName : i.InvitedUsername,
                i.InvitedBy.UserName,
                i.Permissions, null, i.Message,
                i.InvitedUserId == null && i.InvitedUsername == null,
                i.CreatedAt, i.ExpiresAt, i.AcceptedAt, i.RevokedAt))
            .ToListAsync(ct));

        invitations.AddRange(await db.TeamInvitations.AsNoTracking()
            .Where(i => sent ? i.InvitedByUserId == id : i.InvitedUserId == id)
            .Select(i => new Invitation(
                "team", i.Id, i.TeamId, i.Team.Name,
                i.InvitedUser != null ? i.InvitedUser.UserName : i.InvitedUsername,
                i.InvitedBy.UserName,
                null, i.Role, i.Message,
                i.InvitedUsername == null,
                i.CreatedAt, i.ExpiresAt, i.AcceptedAt, i.RevokedAt))
            .ToListAsync(ct));

        invitations.AddRange(await db.ContentBundleInvitations.AsNoTracking()
            .Where(i => sent ? i.InvitedByUserId == id : i.InvitedUserId == id)
            .Select(i => new Invitation(
                "bundle", i.Id, i.BundleId, i.Bundle.Name,
                i.InvitedUser != null ? i.InvitedUser.UserName : i.InvitedUsername,
                i.InvitedBy.UserName,
                i.Permissions, null, i.Message,
                i.InvitedUserId == null && i.InvitedUsername == null,
                i.CreatedAt, i.ExpiresAt, i.AcceptedAt, i.RevokedAt))
            .ToListAsync(ct));

        return invitations.OrderBy(i => i.CreatedAt).ToList();
    }

    // ── document shape ───────────────────────────────────────────────────────

    private sealed record Document(
        int Format,
        DateTimeOffset ExportedAt,
        Profile Profile,
        List<OwnedPack> Packs,
        List<HostedItem> HostedItems,
        List<TeamMembership> Teams,
        List<Access> AccessToOthersItems,
        List<LibraryEntry> Library,
        List<Invitation> InvitationsSent,
        List<Invitation> InvitationsReceived,
        List<ActivityLine> Activity,
        bool ActivityTruncated);

    private sealed record Profile(
        Guid Id, string Username, string? Email, bool EmailConfirmed, DateTimeOffset CreatedAt, bool IsAdmin,
        DateTimeOffset? TermsAcceptedAt, string? TermsVersion, bool GoogleLinked, bool HasPassword,
        long? StorageQuotaBytes);

    private sealed record OwnedPack(
        Guid Id, string Name, string? Summary, string? Description, PackVisibility Visibility,
        bool IsShared, bool IsEmpty, string? MinecraftVersion, LoaderKind Loader, string? LoaderVersion,
        string? RulesJson, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, List<PackFile> Files);

    private sealed record PackFile(string Path, string Hash, long Size);

    /// <param name="Kind">mod, world, resourcepack or bundle.</param>
    /// <param name="BundleKind">For bundles only: shader pack, config, and so on.</param>
    /// <param name="TargetFolder">For bundles only: the instance folder it unpacks into.</param>
    private sealed record HostedItem(
        string Kind, Guid Id, string Slug, string Name, string? Summary, string? Description,
        PackVisibility Visibility, BundleKind? BundleKind, string? TargetFolder, string? IconHash,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, List<ItemVersion> Versions);

    private sealed record ItemVersion(
        Guid Id, string Version, string? Channel, string? Changelog,
        string FileName, long FileSize, string Hash, DateTimeOffset PublishedAt);

    private sealed record TeamMembership(
        Guid Id, string Name, string Owner, TeamRole Role, DateTimeOffset? JoinedAt, int Members);

    /// <param name="Via">"direct" for a grant to the user, "team" for one to a team they are in.</param>
    private sealed record Access(
        string Kind, Guid Id, string Name, string Owner, PackPermissions Permissions, string Via, string? Team);

    private sealed record LibraryEntry(Guid PackId, string Name, string Owner, DateTimeOffset AddedAt);

    /// <param name="Permissions">What an invitation to an item offers. Null for teams.</param>
    /// <param name="Role">The role a team invitation offers. Null for items.</param>
    private sealed record Invitation(
        string Kind, Guid Id, Guid SubjectId, string SubjectName, string? Invitee, string? InvitedBy,
        PackPermissions? Permissions, TeamRole? Role, string? Message, bool IsLink,
        DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? AcceptedAt, DateTimeOffset? RevokedAt);

    private sealed record ActivityLine(
        Guid Id, DateTimeOffset At, ActivityKind Kind, ActivitySubjectType SubjectType, Guid SubjectId,
        string SubjectName, string? Actor, string? Target, string? TargetTeam, string? Detail);
}
