using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewCostAndCachedInputPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "cost_is_approximate",
                table: "review_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "total_estimated_cost_usd",
                table: "review_jobs",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "estimated_cost_usd",
                table: "client_token_usage_samples",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "cached_input_cost_per_1m_usd",
                table: "ai_configured_models",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cost_is_approximate",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "total_estimated_cost_usd",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "estimated_cost_usd",
                table: "client_token_usage_samples");

            migrationBuilder.DropColumn(
                name: "cached_input_cost_per_1m_usd",
                table: "ai_configured_models");
        }
    }
}
