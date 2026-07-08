using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudLauncher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedWorldEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SharedWorlds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Summary = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Description = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    McVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    IconBlobHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedWorlds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedWorlds_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SharedWorldCollaborators",
                columns: table => new
                {
                    WorldId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedWorldCollaborators", x => new { x.WorldId, x.UserId });
                    table.ForeignKey(
                        name: "FK_SharedWorldCollaborators_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SharedWorldCollaborators_SharedWorlds_WorldId",
                        column: x => x.WorldId,
                        principalTable: "SharedWorlds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SharedWorldTeams",
                columns: table => new
                {
                    WorldId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedWorldTeams", x => new { x.WorldId, x.TeamId });
                    table.ForeignKey(
                        name: "FK_SharedWorldTeams_SharedWorlds_WorldId",
                        column: x => x.WorldId,
                        principalTable: "SharedWorlds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SharedWorldTeams_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SharedWorldVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorldId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionString = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Changelog = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    BlobHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    McVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedWorldVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SharedWorldVersions_SharedWorlds_WorldId",
                        column: x => x.WorldId,
                        principalTable: "SharedWorlds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SharedWorldCollaborators_UserId",
                table: "SharedWorldCollaborators",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedWorlds_OwnerId",
                table: "SharedWorlds",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedWorlds_Slug",
                table: "SharedWorlds",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SharedWorldTeams_TeamId",
                table: "SharedWorldTeams",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_SharedWorldVersions_WorldId",
                table: "SharedWorldVersions",
                column: "WorldId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SharedWorldCollaborators");

            migrationBuilder.DropTable(
                name: "SharedWorldTeams");

            migrationBuilder.DropTable(
                name: "SharedWorldVersions");

            migrationBuilder.DropTable(
                name: "SharedWorlds");
        }
    }
}
