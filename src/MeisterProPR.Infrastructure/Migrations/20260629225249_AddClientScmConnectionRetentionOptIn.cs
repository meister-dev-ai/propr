using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientScmConnectionRetentionOptIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "retention_days",
                table: "client_scm_connections",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "store_diffs",
                table: "client_scm_connections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "store_threads",
                table: "client_scm_connections",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "retention_days",
                table: "client_scm_connections");

            migrationBuilder.DropColumn(
                name: "store_diffs",
                table: "client_scm_connections");

            migrationBuilder.DropColumn(
                name: "store_threads",
                table: "client_scm_connections");
        }
    }
}
