using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudLauncher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPackDiscordLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PackDiscordLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PackId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BoardId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    WebhookId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    WebhookToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CodeExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LinkedByDiscordUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    GuildId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ChannelId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ViewKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PublishedRevision = table.Column<long>(type: "bigint", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MessagesJson = table.Column<string>(type: "text", nullable: true),
                    RefreshRequested = table.Column<bool>(type: "boolean", nullable: false),
                    Broken = table.Column<bool>(type: "boolean", nullable: false),
                    LastError = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    LastErrorAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackDiscordLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackDiscordLinks_Packs_PackId",
                        column: x => x.PackId,
                        principalTable: "Packs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PackDiscordLinks_ChannelId",
                table: "PackDiscordLinks",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_PackDiscordLinks_Code",
                table: "PackDiscordLinks",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PackDiscordLinks_PackId",
                table: "PackDiscordLinks",
                column: "PackId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PackDiscordLinks");
        }
    }
}
