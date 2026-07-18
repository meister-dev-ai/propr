using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CaptureFullTokenUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "total_cache_write_tokens_aggregated",
                table: "review_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "total_cached_input_tokens_aggregated",
                table: "review_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "total_reasoning_tokens_aggregated",
                table: "review_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "cache_write_tokens",
                table: "client_token_usage_samples",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "cached_input_tokens",
                table: "client_token_usage_samples",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "reasoning_tokens",
                table: "client_token_usage_samples",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "total_cache_write_tokens_aggregated",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "total_cached_input_tokens_aggregated",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "total_reasoning_tokens_aggregated",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "cache_write_tokens",
                table: "client_token_usage_samples");

            migrationBuilder.DropColumn(
                name: "cached_input_tokens",
                table: "client_token_usage_samples");

            migrationBuilder.DropColumn(
                name: "reasoning_tokens",
                table: "client_token_usage_samples");
        }
    }
}
