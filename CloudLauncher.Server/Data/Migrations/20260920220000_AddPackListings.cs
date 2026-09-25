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
    /// A share only grants access; the user adds the pack to their list themselves. The backfill lists
    /// every pack each user can already see, so existing libraries don't change.
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

            // Backfill: every pack a user can see now stays listed. Collaborator grants first, then team
            // grants, skipping the owner (owners are listed by ownership).
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
