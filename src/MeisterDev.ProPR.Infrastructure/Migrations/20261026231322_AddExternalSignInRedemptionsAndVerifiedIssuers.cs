using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalSignInRedemptionsAndVerifiedIssuers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "issuer_verified",
                table: "external_identities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "external_sign_in_redemptions",
                columns: table => new
                {
                    redemption_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_sign_in_redemptions", x => x.redemption_key);
                });

            migrationBuilder.CreateIndex(
                name: "ix_external_sign_in_redemptions_expires_at",
                table: "external_sign_in_redemptions",
                column: "expires_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "external_sign_in_redemptions");

            migrationBuilder.DropColumn(
                name: "issuer_verified",
                table: "external_identities");
        }
    }
}
