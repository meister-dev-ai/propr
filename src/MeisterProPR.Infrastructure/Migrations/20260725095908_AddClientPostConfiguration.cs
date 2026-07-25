using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientPostConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "auto_resolve_severities",
                table: "clients",
                type: "text",
                nullable: false,
                defaultValueSql: "''");

            migrationBuilder.AddColumn<int>(
                name: "minimum_severity_to_post",
                table: "clients",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_resolve_severities",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "minimum_severity_to_post",
                table: "clients");
        }
    }
}
