using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReviewHistoryReadPerformance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "reviewed_file_count",
                table: "review_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_thread_memory_records_client_pr_updated_at",
                table: "thread_memory_records",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "updated_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_thread_memory_records_client_updated_at",
                table: "thread_memory_records",
                columns: new[] { "client_id", "updated_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_review_jobs_submitted_at",
                table: "review_jobs",
                column: "submitted_at",
                descending: new bool[0]);

            // Backfill the count for jobs that already have file results. Readers fall back to counting the
            // file results when the column is null, so this is an optimisation rather than a correctness
            // step: without it every pre-existing job would keep paying that fallback on every list read.
            // Jobs with no file results stay null, which is what a job that never dispatched any should read as.
            migrationBuilder.Sql(
                """
                UPDATE review_jobs j
                SET reviewed_file_count = counted.reviewed
                FROM (
                    SELECT job_id,
                           count(*) FILTER (
                               WHERE is_complete
                                 AND NOT is_failed
                                 AND NOT is_excluded
                                 AND NOT is_carried_forward) AS reviewed
                    FROM review_file_results
                    GROUP BY job_id
                ) AS counted
                WHERE counted.job_id = j.id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_thread_memory_records_client_pr_updated_at",
                table: "thread_memory_records");

            migrationBuilder.DropIndex(
                name: "ix_thread_memory_records_client_updated_at",
                table: "thread_memory_records");

            migrationBuilder.DropIndex(
                name: "ix_review_jobs_submitted_at",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "reviewed_file_count",
                table: "review_jobs");
        }
    }
}
