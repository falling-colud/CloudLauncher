using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudLauncher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHostedResourcePackEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostedResourcePacks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Summary = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Description = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    McVersionsCsv = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    IconBlobHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostedResourcePacks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostedResourcePacks_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HostedResourcePackCollaborators",
                columns: table => new
                {
                    ResourcePackId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostedResourcePackCollaborators", x => new { x.ResourcePackId, x.UserId });
                    table.ForeignKey(
                        name: "FK_HostedResourcePackCollaborators_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HostedResourcePackCollaborators_HostedResourcePacks_Resourc~",
                        column: x => x.ResourcePackId,
                        principalTable: "HostedResourcePacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HostedResourcePackTeams",
                columns: table => new
                {
                    ResourcePackId = table.Column<Guid>(type: "uuid", nullable: false),
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    Permissions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostedResourcePackTeams", x => new { x.ResourcePackId, x.TeamId });
                    table.ForeignKey(
                        name: "FK_HostedResourcePackTeams_HostedResourcePacks_ResourcePackId",
                        column: x => x.ResourcePackId,
                        principalTable: "HostedResourcePacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HostedResourcePackTeams_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HostedResourcePackVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourcePackId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionString = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Changelog = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    ReleaseChannel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BlobHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    McVersionsCsv = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostedResourcePackVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostedResourcePackVersions_HostedResourcePacks_ResourcePack~",
                        column: x => x.ResourcePackId,
                        principalTable: "HostedResourcePacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostedResourcePackCollaborators_UserId",
                table: "HostedResourcePackCollaborators",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_HostedResourcePacks_OwnerId",
                table: "HostedResourcePacks",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_HostedResourcePacks_Slug",
                table: "HostedResourcePacks",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HostedResourcePackTeams_TeamId",
                table: "HostedResourcePackTeams",
                column: "TeamId");

            migrationBuilder.CreateIndex(
                name: "IX_HostedResourcePackVersions_ResourcePackId",
                table: "HostedResourcePackVersions",
                column: "ResourcePackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostedResourcePackCollaborators");

            migrationBuilder.DropTable(
                name: "HostedResourcePackTeams");

            migrationBuilder.DropTable(
                name: "HostedResourcePackVersions");

            migrationBuilder.DropTable(
                name: "HostedResourcePacks");
        }
    }
}
