using System;
using CloudLauncher.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CloudLauncher.Server.Data.Migrations
{
    /// <summary>
    /// Terms acceptance on accounts, the rotation link on refresh tokens, and the admin switch that
    /// closes sign-ups.
    /// </summary>
    /// <remarks>
    /// Every column is nullable or defaults to how things already behaved: existing accounts have no
    /// recorded acceptance, existing revoked tokens do not count as rotated for reuse detection, and
    /// sign-ups stay open.
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260925120000_AddAccountControls")]
    public partial class AddAccountControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TermsAcceptedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TermsVersion",
                table: "AspNetUsers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplacedById",
                table: "RefreshTokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RegistrationClosed",
                table: "GlobalSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "TermsAcceptedAt", table: "AspNetUsers");
            migrationBuilder.DropColumn(name: "TermsVersion", table: "AspNetUsers");
            migrationBuilder.DropColumn(name: "ReplacedById", table: "RefreshTokens");
            migrationBuilder.DropColumn(name: "RegistrationClosed", table: "GlobalSettings");
        }
    }
}
