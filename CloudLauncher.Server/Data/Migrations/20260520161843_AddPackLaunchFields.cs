using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudLauncher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPackLaunchFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEmpty",
                table: "Packs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Loader",
                table: "Packs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LoaderVersion",
                table: "Packs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MinecraftVersion",
                table: "Packs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsEmpty",
                table: "Packs");

            migrationBuilder.DropColumn(
                name: "Loader",
                table: "Packs");

            migrationBuilder.DropColumn(
                name: "LoaderVersion",
                table: "Packs");

            migrationBuilder.DropColumn(
                name: "MinecraftVersion",
                table: "Packs");
        }
    }
}
