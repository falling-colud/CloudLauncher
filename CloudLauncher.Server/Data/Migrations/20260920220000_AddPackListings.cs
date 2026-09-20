using System;
using CloudLauncher.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudLauncher.Server.Data.Migrations
{
    /// <summary>
    /// Separates "I have access to this pack" from "this pack is in my instance list".
    /// </summary>
    /// <remarks>
    /// Until now a collaborator or team grant did both, so the moment somebody shared a pack with you
    /// it appeared in your library unasked — and taking it out meant deleting the grant. The backfill
    /// below lists every pack every user can currently see, so nobody's library changes when this
    /// lands; from here on, a share grants access and the user adds it themselves.
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260920220000_AddPackListings")]
    public partial class AddPackListings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PackListings",
                columns: table => new
                {
                    PackId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackListings", x => new { x.PackId, x.UserId });
                    table.ForeignKey(
                        name: "FK_PackListings_Packs_PackId",
                        column: x => x.PackId,
                        principalTable: "Packs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PackListings_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PackListings_UserId",
                table: "PackListings",
                column: "UserId");

            // Backfill: everything that was listed a moment ago stays listed. Collaborator grants
            // first, then team grants, skipping the pack's owner (owners are listed by ownership).
            migrationBuilder.Sql("""
                INSERT INTO "PackListings" ("PackId", "UserId", "AddedAt")
                SELECT c."PackId", c."UserId", now()
                FROM "PackCollaborators" c
                JOIN "Packs" p ON p."Id" = c."PackId"
                WHERE p."OwnerId" <> c."UserId"
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.Sql("""
                INSERT INTO "PackListings" ("PackId", "UserId", "AddedAt")
                SELECT pt."PackId", tm."UserId", now()
                FROM "PackTeams" pt
                JOIN "TeamMembers" tm ON tm."TeamId" = pt."TeamId"
                JOIN "Packs" p ON p."Id" = pt."PackId"
                WHERE p."OwnerId" <> tm."UserId"
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PackListings");
        }
    }
}
