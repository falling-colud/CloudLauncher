using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudLauncher.Server.Data.Migrations
{
    /// <summary>
    /// A pack's planning boards, stored apart from its files so they sync live.
    /// </summary>
    /// <remarks>A new table only: nothing existing changes, and a pack has no row until a launcher
    /// first stores its boards.</remarks>
    public partial class AddPackPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PackPlans",
                columns: table => new
                {
                    PackId = table.Column<Guid>(type: "uuid", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackPlans", x => x.PackId);
                    table.ForeignKey(
                        name: "FK_PackPlans_Packs_PackId",
                        column: x => x.PackId,
                        principalTable: "Packs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PackPlans");
        }
    }
}
