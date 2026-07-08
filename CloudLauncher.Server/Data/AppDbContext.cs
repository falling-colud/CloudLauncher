using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using CloudLauncher.Shared;

namespace CloudLauncher.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Pack> Packs => Set<Pack>();
    public DbSet<PackCollaborator> PackCollaborators => Set<PackCollaborator>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<PackTeam> PackTeams => Set<PackTeam>();
    public DbSet<PackManifestEntry> PackManifestEntries => Set<PackManifestEntry>();
    public DbSet<GlobalSetting> GlobalSettings => Set<GlobalSetting>();
    public DbSet<Mod> Mods => Set<Mod>();
    public DbSet<ModVersion> ModVersions => Set<ModVersion>();
    public DbSet<ModCollaborator> ModCollaborators => Set<ModCollaborator>();
    public DbSet<ModTeam> ModTeams => Set<ModTeam>();
    public DbSet<SharedWorld> SharedWorlds => Set<SharedWorld>();
    public DbSet<SharedWorldVersion> SharedWorldVersions => Set<SharedWorldVersion>();
    public DbSet<SharedWorldCollaborator> SharedWorldCollaborators => Set<SharedWorldCollaborator>();
    public DbSet<SharedWorldTeam> SharedWorldTeams => Set<SharedWorldTeam>();
    public DbSet<HostedResourcePack> HostedResourcePacks => Set<HostedResourcePack>();
    public DbSet<HostedResourcePackVersion> HostedResourcePackVersions => Set<HostedResourcePackVersion>();
    public DbSet<HostedResourcePackCollaborator> HostedResourcePackCollaborators => Set<HostedResourcePackCollaborator>();
    public DbSet<HostedResourcePackTeam> HostedResourcePackTeams => Set<HostedResourcePackTeam>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Pack>(e =>
        {
            e.HasIndex(x => x.OwnerId);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(PackText.SummaryMaxLength);
            e.Property(x => x.Description).HasMaxLength(PackText.DescriptionMaxLength);
        });

        b.Entity<PackCollaborator>(e =>
        {
            e.HasKey(x => new { x.PackId, x.UserId });
            e.HasOne(x => x.Pack).WithMany(p => p.Collaborators).HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Team>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
        });

        b.Entity<TeamMember>(e =>
        {
            e.HasKey(x => new { x.TeamId, x.UserId });
            e.HasOne(x => x.Team).WithMany(t => t.Members).HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PackTeam>(e =>
        {
            e.HasKey(x => new { x.PackId, x.TeamId });
            e.HasOne(x => x.Pack).WithMany(p => p.Teams).HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PackManifestEntry>(e =>
        {
            e.HasIndex(x => new { x.PackId, x.RelativePath }).IsUnique();
            e.HasOne(x => x.Pack).WithMany(p => p.ManifestEntries).HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.RelativePath).HasMaxLength(1024).IsRequired();
            e.Property(x => x.Hash).HasMaxLength(64).IsRequired();
        });

        b.Entity<GlobalSetting>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        b.Entity<Mod>(e =>
        {
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(96).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(512);
            e.Property(x => x.Description).HasMaxLength(4096);
            e.Property(x => x.McVersionsCsv).HasMaxLength(512);
            e.Property(x => x.LoadersCsv).HasMaxLength(128);
            e.Property(x => x.IconBlobHash).HasMaxLength(64);
        });

        b.Entity<ModVersion>(e =>
        {
            e.HasIndex(x => x.ModId);
            e.HasOne(x => x.Mod).WithMany(m => m.Versions).HasForeignKey(x => x.ModId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.VersionString).HasMaxLength(64).IsRequired();
            e.Property(x => x.ReleaseChannel).HasMaxLength(16).IsRequired();
            e.Property(x => x.BlobHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.McVersionsCsv).HasMaxLength(512);
            e.Property(x => x.LoadersCsv).HasMaxLength(128);
            e.Property(x => x.Changelog).HasMaxLength(8192);
        });

        b.Entity<ModCollaborator>(e =>
        {
            e.HasKey(x => new { x.ModId, x.UserId });
            e.HasOne(x => x.Mod).WithMany(m => m.Collaborators).HasForeignKey(x => x.ModId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ModTeam>(e =>
        {
            e.HasKey(x => new { x.ModId, x.TeamId });
            e.HasOne(x => x.Mod).WithMany(m => m.Teams).HasForeignKey(x => x.ModId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SharedWorld>(e =>
        {
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(96).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(512);
            e.Property(x => x.Description).HasMaxLength(4096);
            e.Property(x => x.McVersion).HasMaxLength(32);
            e.Property(x => x.IconBlobHash).HasMaxLength(64);
        });

        b.Entity<SharedWorldVersion>(e =>
        {
            e.HasIndex(x => x.WorldId);
            e.HasOne(x => x.World).WithMany(w => w.Versions).HasForeignKey(x => x.WorldId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.VersionString).HasMaxLength(64).IsRequired();
            e.Property(x => x.BlobHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.McVersion).HasMaxLength(32);
            e.Property(x => x.Changelog).HasMaxLength(8192);
        });

        b.Entity<SharedWorldCollaborator>(e =>
        {
            e.HasKey(x => new { x.WorldId, x.UserId });
            e.HasOne(x => x.World).WithMany(w => w.Collaborators).HasForeignKey(x => x.WorldId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SharedWorldTeam>(e =>
        {
            e.HasKey(x => new { x.WorldId, x.TeamId });
            e.HasOne(x => x.World).WithMany(w => w.Teams).HasForeignKey(x => x.WorldId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<HostedResourcePack>(e =>
        {
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(96).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(512);
            e.Property(x => x.Description).HasMaxLength(4096);
            e.Property(x => x.McVersionsCsv).HasMaxLength(512);
            e.Property(x => x.IconBlobHash).HasMaxLength(64);
        });

        b.Entity<HostedResourcePackVersion>(e =>
        {
            e.HasIndex(x => x.ResourcePackId);
            e.HasOne(x => x.ResourcePack).WithMany(m => m.Versions).HasForeignKey(x => x.ResourcePackId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.VersionString).HasMaxLength(64).IsRequired();
            e.Property(x => x.ReleaseChannel).HasMaxLength(16).IsRequired();
            e.Property(x => x.BlobHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.McVersionsCsv).HasMaxLength(512);
            e.Property(x => x.Changelog).HasMaxLength(8192);
        });

        b.Entity<HostedResourcePackCollaborator>(e =>
        {
            e.HasKey(x => new { x.ResourcePackId, x.UserId });
            e.HasOne(x => x.ResourcePack).WithMany(m => m.Collaborators).HasForeignKey(x => x.ResourcePackId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<HostedResourcePackTeam>(e =>
        {
            e.HasKey(x => new { x.ResourcePackId, x.TeamId });
            e.HasOne(x => x.ResourcePack).WithMany(m => m.Teams).HasForeignKey(x => x.ResourcePackId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
