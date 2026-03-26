using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenAggregateToReviewJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "total_input_tokens_aggregated",
                table: "review_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "total_output_tokens_aggregated",
                table: "review_jobs",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "total_input_tokens_aggregated",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "total_output_tokens_aggregated",
                table: "review_jobs");
        }
    }
}
