using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFindingRiskAssessments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_code_insight_daily_counts_key",
                table: "code_insight_daily_counts");

            migrationBuilder.AddColumn<short>(
                name: "Impact",
                table: "reviewer_performance_daily_counts",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "Likelihood",
                table: "reviewer_performance_daily_counts",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string>(
                name: "RiskRubricVersion",
                table: "reviewer_performance_daily_counts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<short>(
                name: "RiskSeverity",
                table: "reviewer_performance_daily_counts",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedRiskAssessment",
                table: "code_insight_findings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "Impact",
                table: "code_insight_findings",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "Likelihood",
                table: "code_insight_findings",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string>(
                name: "RiskRubricVersion",
                table: "code_insight_findings",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<short>(
                name: "RiskSeverity",
                table: "code_insight_findings",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "Impact",
                table: "code_insight_daily_counts",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "Likelihood",
                table: "code_insight_daily_counts",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string>(
                name: "RiskRubricVersion",
                table: "code_insight_daily_counts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<short>(
                name: "RiskSeverity",
                table: "code_insight_daily_counts",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_daily_counts_key",
                table: "code_insight_daily_counts",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "file_path", "job_id", "bucket_date", "dimension", "dimension_key", "Impact", "Likelihood", "RiskSeverity", "RiskRubricVersion" },
                unique: true);

            // Historical observations have no risk evidence. Preserve their totals and expose Unknown in each distribution.
            migrationBuilder.Sql("""
                INSERT INTO code_insight_daily_counts
                    (id, client_id, repository_id, pull_request_id, file_path, job_id, bucket_date,
                     dimension, dimension_key, count, updated_at)
                SELECT gen_random_uuid(), original.client_id, original.repository_id, original.pull_request_id,
                       original.file_path, original.job_id, original.bucket_date,
                       risk.dimension, 'Unknown', original.count, original.updated_at
                FROM code_insight_daily_counts AS original
                CROSS JOIN (VALUES (4::smallint), (5::smallint), (6::smallint)) AS risk(dimension)
                WHERE original.dimension = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rebuild the derived quality cache after downgrade; risk cells can share the previous narrower key.
            migrationBuilder.Sql("DELETE FROM code_insight_daily_counts;");

            migrationBuilder.DropIndex(
                name: "uq_code_insight_daily_counts_key",
                table: "code_insight_daily_counts");

            migrationBuilder.DropColumn(
                name: "Impact",
                table: "reviewer_performance_daily_counts");

            migrationBuilder.DropColumn(
                name: "Likelihood",
                table: "reviewer_performance_daily_counts");

            migrationBuilder.DropColumn(
                name: "RiskRubricVersion",
                table: "reviewer_performance_daily_counts");

            migrationBuilder.DropColumn(
                name: "RiskSeverity",
                table: "reviewer_performance_daily_counts");

            migrationBuilder.DropColumn(
                name: "EncryptedRiskAssessment",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "Impact",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "Likelihood",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "RiskRubricVersion",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "RiskSeverity",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "Impact",
                table: "code_insight_daily_counts");

            migrationBuilder.DropColumn(
                name: "Likelihood",
                table: "code_insight_daily_counts");

            migrationBuilder.DropColumn(
                name: "RiskRubricVersion",
                table: "code_insight_daily_counts");

            migrationBuilder.DropColumn(
                name: "RiskSeverity",
                table: "code_insight_daily_counts");

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_daily_counts_key",
                table: "code_insight_daily_counts",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "file_path", "job_id", "bucket_date", "dimension", "dimension_key" },
                unique: true);
        }
    }
}
