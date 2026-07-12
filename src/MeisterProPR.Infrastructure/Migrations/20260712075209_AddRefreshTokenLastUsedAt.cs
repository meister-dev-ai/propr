using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenLastUsedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add nullable first so existing rows can be backfilled from their issuance time, then make it
            // required. Seeding last_used_at from created_at means the new idle rule is applied by session
            // age: sessions issued within the idle window survive, older ones lapse on next refresh.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_used_at",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE refresh_tokens SET last_used_at = created_at WHERE last_used_at IS NULL;");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "last_used_at",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_used_at",
                table: "refresh_tokens");
        }
    }
}
