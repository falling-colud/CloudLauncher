using System;
using CloudLauncher.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudLauncher.Server.Data.Migrations
{
    /// <summary>Sharing: content bundles, invitations and share links, team roles and an activity
    /// log.</summary>
    /// <remarks>Also makes team names unique per owner instead of globally, and makes
    /// <c>Teams.OwnerId</c> cascade like the rest of the model so a user who owns a team can be
    /// deleted.</remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260921000000_AddSharingAndBundles")]
    public partial class AddSharingAndBundles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── content bundles ──────────────────────────────────────────────

            migrationBuilder.CreateTable(
                name: "ContentBundles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Slug = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Summary = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Description = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    TargetPathRoot = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    McVersionsCsv = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LoadersCsv = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IconBlobHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DownloadCount = table.Column<long>(type: "bigint", nullable: false),
                    ShareToken = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ShareTokenCreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentBundles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentBundles_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentBundleVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BundleId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionString = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Changelog = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    ReleaseChannel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BlobHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    McVersionsCsv = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LoadersCsv = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentBundleVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentBundleVersions_ContentBundles_BundleId",
                        column: x => x.BundleId,
                        principalTable: "ContentBundles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentBundleCollaborators",
                columns: table => new
                {
                    BundleId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentBundleCollaborators", x => new { x.BundleId, x.UserId });
                    table.ForeignKey(
                        name: "FK_ContentBundleCollaborators_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentBundleCollaborators_ContentBundles_BundleId",
                        column: x => x.BundleId,
                        principalTable: "ContentBundles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentBundleTeams",
                columns: table => new
                {
                    BundleId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentBundleTeams", x => new { x.BundleId, x.TeamId });
                    table.ForeignKey(
                        name: "FK_ContentBundleTeams_ContentBundles_BundleId",
                        column: x => x.BundleId,
                        principalTable: "ContentBundles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentBundleTeams_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // ── invitations ──────────────────────────────────────────────────

            migrationBuilder.CreateTable(
                name: "PackInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackId = table.Column<Guid>(type: "uuid", nullable: false),
                    // Nullable: an invitation created as a share link has no target user until
                    // someone redeems it.
                    InvitedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    InvitedUsername = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackInvitations_AspNetUsers_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PackInvitations_AspNetUsers_InvitedUserId",
                        column: x => x.InvitedUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PackInvitations_Packs_PackId",
                        column: x => x.PackId,
                        principalTable: "Packs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvitedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    InvitedUsername = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TeamInvitations_AspNetUsers_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamInvitations_AspNetUsers_InvitedUserId",
                        column: x => x.InvitedUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamInvitations_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentBundleInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BundleId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvitedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    InvitedUsername = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    InvitedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false),
                    Token = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentBundleInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentBundleInvitations_AspNetUsers_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentBundleInvitations_AspNetUsers_InvitedUserId",
                        column: x => x.InvitedUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentBundleInvitations_ContentBundles_BundleId",
                        column: x => x.BundleId,
                        principalTable: "ContentBundles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // ── activity ─────────────────────────────────────────────────────

            migrationBuilder.CreateTable(
                name: "ActivityEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    SubjectType = table.Column<int>(type: "integer", nullable: false),
                    // Not a foreign key: the subject may be deleted and the line still has to
                    // render, hence the denormalised SubjectName.
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetTeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    Detail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivityEntries_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActivityEntries_AspNetUsers_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ActivityEntries_Teams_TargetTeamId",
                        column: x => x.TargetTeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            // ── indexes for the new tables ───────────────────────────────────

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_ActorUserId_CreatedAt",
                table: "ActivityEntries",
                columns: new[] { "ActorUserId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_SubjectType_SubjectId_CreatedAt",
                table: "ActivityEntries",
                columns: new[] { "SubjectType", "SubjectId", "CreatedAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_TargetTeamId",
                table: "ActivityEntries",
                column: "TargetTeamId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_TargetUserId",
                table: "ActivityEntries",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentBundleCollaborators_UserId",
                table: "ContentBundleCollaborators",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentBundleInvitations_BundleId",
                table: "ContentBundleInvitations",
                column: "BundleId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentBundleInvitations_InvitedByUserId",
                table: "ContentBundleInvitations",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentBundleInvitations_InvitedUserId_AcceptedAt",
                table: "ContentBundleInvitations",
                columns: new[] { "InvitedUserId", "AcceptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ContentBundleInvitations_Token",
                table: "ContentBundleInvitations",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentBundles_Kind_Slug",
                table: "ContentBundles",
                columns: new[] { "Kind", "Slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentBundles_OwnerId",
                table: "ContentBundles",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentBundles_ShareToken",
                table: "ContentBundles",
                column: "ShareToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContentBundleTeams_TeamId",
                table: "ContentBundleTeams",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentBundleVersions_BundleId",
                table: "ContentBundleVersions",
                column: "BundleId");

            migrationBuilder.CreateIndex(
                name: "IX_PackInvitations_InvitedByUserId",
                table: "PackInvitations",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PackInvitations_InvitedUserId_AcceptedAt",
                table: "PackInvitations",
                columns: new[] { "InvitedUserId", "AcceptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PackInvitations_PackId",
                table: "PackInvitations",
                column: "PackId");

            migrationBuilder.CreateIndex(
                name: "IX_PackInvitations_Token",
                table: "PackInvitations",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_InvitedByUserId",
                table: "TeamInvitations",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_InvitedUserId_AcceptedAt",
                table: "TeamInvitations",
                columns: new[] { "InvitedUserId", "AcceptedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_TeamId",
                table: "TeamInvitations",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamInvitations_Token",
                table: "TeamInvitations",
                column: "Token",
                unique: true);

            // ── new columns on existing tables ───────────────────────────────

            migrationBuilder.AddColumn<string>(
                name: "ShareToken",
                table: "Packs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ShareTokenCreatedAt",
                table: "Packs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastUploadedById",
                table: "Packs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastUploadedAt",
                table: "Packs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Packs_LastUploadedById",
                table: "Packs",
                column: "LastUploadedById");

            migrationBuilder.CreateIndex(
                name: "IX_Packs_ShareToken",
                table: "Packs",
                column: "ShareToken",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Packs_AspNetUsers_LastUploadedById",
                table: "Packs",
                column: "LastUploadedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Every existing row is a plain member; the owner is promoted by the backfill below.
            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "TeamMembers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // TeamRole.Owner = 2. Team.OwnerId stays the source of truth; this copies it onto the
            // membership row so a member list renders from one column.
            migrationBuilder.Sql("""
                UPDATE "TeamMembers" tm
                SET "Role" = 2
                FROM "Teams" t
                WHERE t."Id" = tm."TeamId" AND t."OwnerId" = tm."UserId";
                """);

            // ── team name scoping and owner delete behaviour ─────────────────

            // IX_Teams_OwnerId goes because the new composite leads with OwnerId and covers it.
            migrationBuilder.DropIndex(name: "IX_Teams_Name", table: "Teams");
            migrationBuilder.DropIndex(name: "IX_Teams_OwnerId", table: "Teams");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_OwnerId_Name",
                table: "Teams",
                columns: new[] { "OwnerId", "Name" },
                unique: true);

            migrationBuilder.DropForeignKey(name: "FK_Teams_AspNetUsers_OwnerId", table: "Teams");
            migrationBuilder.AddForeignKey(
                name: "FK_Teams_AspNetUsers_OwnerId",
                table: "Teams",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ── team owner delete behaviour and name scoping ─────────────────

            migrationBuilder.DropForeignKey(name: "FK_Teams_AspNetUsers_OwnerId", table: "Teams");
            migrationBuilder.AddForeignKey(
                name: "FK_Teams_AspNetUsers_OwnerId",
                table: "Teams",
                column: "OwnerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropIndex(name: "IX_Teams_OwnerId_Name", table: "Teams");

            // Going back makes names globally unique again, and two people may have created a team
            // called "Friends" since. Rename the later ones first so the rollback can run. The id
            // fragment keeps the result unique; Name is varchar(64) and 55 + 2 + 6 + 1 = 64.
            migrationBuilder.Sql("""
                WITH dupes AS (
                    SELECT "Id",
                           row_number() OVER (PARTITION BY "Name" ORDER BY "CreatedAt", "Id") AS rn
                    FROM "Teams"
                )
                UPDATE "Teams" t
                SET "Name" = left(t."Name", 55) || ' (' || substr(t."Id"::text, 1, 6) || ')'
                FROM dupes d
                WHERE d."Id" = t."Id" AND d.rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Teams_Name",
                table: "Teams",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Teams_OwnerId",
                table: "Teams",
                column: "OwnerId");

            // ── columns added to existing tables ─────────────────────────────

            migrationBuilder.DropColumn(name: "Role", table: "TeamMembers");

            migrationBuilder.DropForeignKey(name: "FK_Packs_AspNetUsers_LastUploadedById", table: "Packs");
            migrationBuilder.DropIndex(name: "IX_Packs_ShareToken", table: "Packs");
            migrationBuilder.DropIndex(name: "IX_Packs_LastUploadedById", table: "Packs");
            migrationBuilder.DropColumn(name: "LastUploadedAt", table: "Packs");
            migrationBuilder.DropColumn(name: "LastUploadedById", table: "Packs");
            migrationBuilder.DropColumn(name: "ShareTokenCreatedAt", table: "Packs");
            migrationBuilder.DropColumn(name: "ShareToken", table: "Packs");

            // ── new tables, dependents first ─────────────────────────────────

            migrationBuilder.DropTable(name: "ActivityEntries");
            migrationBuilder.DropTable(name: "ContentBundleInvitations");
            migrationBuilder.DropTable(name: "TeamInvitations");
            migrationBuilder.DropTable(name: "PackInvitations");
            migrationBuilder.DropTable(name: "ContentBundleCollaborators");
            migrationBuilder.DropTable(name: "ContentBundleTeams");
            migrationBuilder.DropTable(name: "ContentBundleVersions");
            migrationBuilder.DropTable(name: "ContentBundles");
        }
    }
}
