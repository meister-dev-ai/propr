using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProtocolCacheWriteAndReasoningTotals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "total_cache_write_tokens",
                table: "review_job_protocols",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "total_reasoning_tokens",
                table: "review_job_protocols",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "total_cache_write_tokens",
                table: "review_job_protocols");

            migrationBuilder.DropColumn(
                name: "total_reasoning_tokens",
                table: "review_job_protocols");
        }
    }
}
