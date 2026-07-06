using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientEnableLanguageRobustScreening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "enable_language_robust_screening",
                table: "clients",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "enable_language_robust_screening",
                table: "clients");
        }
    }
}
