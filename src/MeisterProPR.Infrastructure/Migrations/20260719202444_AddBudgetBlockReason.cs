using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetBlockReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "budget_block_cap_kind",
                table: "review_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "budget_block_scope",
                table: "review_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "budget_block_spent_usd",
                table: "review_jobs",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "budget_block_threshold_usd",
                table: "review_jobs",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "budget_block_cap_kind",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "budget_block_scope",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "budget_block_spent_usd",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "budget_block_threshold_usd",
                table: "review_jobs");
        }
    }
}
