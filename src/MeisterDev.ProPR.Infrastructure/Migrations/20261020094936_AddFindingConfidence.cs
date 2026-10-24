// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFindingConfidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_code_insight_daily_counts_key",
                table: "code_insight_daily_counts");

            migrationBuilder.AddColumn<short>(
                name: "Confidence",
                table: "reviewer_performance_daily_counts",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "Confidence",
                table: "code_insight_findings",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "Confidence",
                table: "code_insight_daily_counts",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_daily_counts_key",
                table: "code_insight_daily_counts",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "file_path", "job_id", "bucket_date", "dimension", "dimension_key", "Impact", "Likelihood", "RiskSeverity", "Confidence", "RiskRubricVersion" },
                unique: true);

            // Retain every historical joint risk cell while exposing its unassessed confidence distribution.
            migrationBuilder.Sql("""
                INSERT INTO code_insight_daily_counts
                    (id, client_id, repository_id, pull_request_id, file_path, job_id, bucket_date,
                     dimension, dimension_key, count, updated_at, "Impact", "Likelihood", "RiskSeverity", "RiskRubricVersion", "Confidence")
                SELECT gen_random_uuid(), original.client_id, original.repository_id, original.pull_request_id,
                       original.file_path, original.job_id, original.bucket_date,
                       7::smallint, 'Unknown', original.count, original.updated_at,
                       original."Impact", original."Likelihood", original."RiskSeverity", original."RiskRubricVersion", 0::smallint
                FROM code_insight_daily_counts AS original
                WHERE original.dimension = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Clear derived cells before narrowing keys; retained findings and report snapshots remain available.
            migrationBuilder.Sql("""
                DELETE FROM code_insight_daily_counts;
                DELETE FROM reviewer_performance_daily_counts;
                UPDATE code_insight_pull_requests
                SET "PerformanceProjectionVersion" = 0, "PerformanceProjectedAt" = NULL;
                """);

            migrationBuilder.DropIndex(
                name: "uq_code_insight_daily_counts_key",
                table: "code_insight_daily_counts");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "reviewer_performance_daily_counts");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "code_insight_daily_counts");

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_daily_counts_key",
                table: "code_insight_daily_counts",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "file_path", "job_id", "bucket_date", "dimension", "dimension_key", "Impact", "Likelihood", "RiskSeverity", "RiskRubricVersion" },
                unique: true);
        }
    }
}
