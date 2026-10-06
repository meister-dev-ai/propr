using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QualifyReviewerPerformanceMissSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_code_insight_misses_thread",
                table: "code_insight_misses");

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_misses_thread",
                table: "code_insight_misses",
                columns: new[] { "code_insight_pull_request_id", "ProviderScope", "provider_thread_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_code_insight_misses_thread",
                table: "code_insight_misses");

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_misses_thread",
                table: "code_insight_misses",
                columns: new[] { "code_insight_pull_request_id", "provider_thread_id" },
                unique: true);
        }
    }
}
