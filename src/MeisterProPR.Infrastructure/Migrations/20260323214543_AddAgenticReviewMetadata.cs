using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgenticReviewMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "confidence_evaluations",
                table: "review_jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "final_confidence",
                table: "review_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tool_call_count",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "tool_calls",
                table: "review_jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "custom_system_message",
                table: "clients",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "confidence_evaluations",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "final_confidence",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "tool_call_count",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "tool_calls",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "custom_system_message",
                table: "clients");
        }
    }
}
