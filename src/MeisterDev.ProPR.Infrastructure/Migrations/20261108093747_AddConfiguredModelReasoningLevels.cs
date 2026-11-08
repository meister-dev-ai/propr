using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConfiguredModelReasoningLevels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "reasoning_levels",
                table: "ai_model_catalog_entries",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "reasoning_levels",
                table: "ai_configured_models",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reasoning_levels_source",
                table: "ai_configured_models",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reasoning_levels",
                table: "ai_model_catalog_entries");

            migrationBuilder.DropColumn(
                name: "reasoning_levels",
                table: "ai_configured_models");

            migrationBuilder.DropColumn(
                name: "reasoning_levels_source",
                table: "ai_configured_models");
        }
    }
}
