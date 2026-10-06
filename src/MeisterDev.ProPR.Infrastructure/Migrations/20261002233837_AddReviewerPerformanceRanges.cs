using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewerPerformanceRanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MissHarvestObservedAt",
                table: "code_insight_pull_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MissHarvestProviderScope",
                table: "code_insight_pull_requests",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "MissHarvestSettled",
                table: "code_insight_pull_requests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PerformanceProjectedAt",
                table: "code_insight_pull_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PerformanceProjectionVersion",
                table: "code_insight_pull_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DimensionClassifierVersion",
                table: "code_insight_misses",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DimensionConfidence",
                table: "code_insight_misses",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailedJudgementAttempts",
                table: "code_insight_misses",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "JudgementFailed",
                table: "code_insight_misses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProviderScope",
                table: "code_insight_misses",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<short>(
                name: "Qualifier",
                table: "code_insight_misses",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceFingerprint",
                table: "code_insight_misses",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TypeMembership",
                table: "code_insight_misses",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "CurrentClassifierConfidence",
                table: "code_insight_findings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentClassifierVersion",
                table: "code_insight_findings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "CurrentCodeChange",
                table: "code_insight_findings",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<short>(
                name: "CurrentDisposition",
                table: "code_insight_findings",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DuplicateOfPublicationId",
                table: "code_insight_findings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "DuplicateState",
                table: "code_insight_findings",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<double>(
                name: "DuplicateVerificationConfidence",
                table: "code_insight_findings",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DuplicateVerificationSource",
                table: "code_insight_findings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DuplicateVerifiedAt",
                table: "code_insight_findings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchedProviderThreadId",
                table: "code_insight_findings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NativeStatus",
                table: "code_insight_findings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutcomeJudgementAttempts",
                table: "code_insight_findings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "OutcomeObservedAt",
                table: "code_insight_findings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PerformanceEvidenceUpdatedAt",
                table: "code_insight_findings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderScope",
                table: "code_insight_findings",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PublicationReason",
                table: "code_insight_findings",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "PublicationState",
                table: "code_insight_findings",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string>(
                name: "NativeStatus",
                table: "code_insight_finding_dispositions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "code_insight_review_exposures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeInsightPullRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    FilePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    RevisionKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LogicalModelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProviderScope = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_review_exposures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_insight_review_exposures_code_insight_pull_requests_Co~",
                        column: x => x.CodeInsightPullRequestId,
                        principalTable: "code_insight_pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reviewer_performance_daily_counts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CellKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CodeInsightPullRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProviderScope = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    PullRequestId = table.Column<long>(type: "bigint", nullable: false),
                    BucketDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ModelId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LogicalModelName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TypeMembership = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Qualifier = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicationState = table.Column<short>(type: "smallint", nullable: false),
                    DuplicateState = table.Column<short>(type: "smallint", nullable: false),
                    IsMiss = table.Column<bool>(type: "boolean", nullable: false),
                    IsClassified = table.Column<bool>(type: "boolean", nullable: false),
                    Count = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviewer_performance_daily_counts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reviewer_performance_daily_counts_code_insight_pull_request~",
                        column: x => x.CodeInsightPullRequestId,
                        principalTable: "code_insight_pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reviewer_performance_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CalculationVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviewer_performance_reports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "reviewer_performance_report_clients",
                columns: table => new
                {
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviewer_performance_report_clients", x => new { x.ReportId, x.ClientId });
                    table.ForeignKey(
                        name: "FK_reviewer_performance_report_clients_clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_reviewer_performance_report_clients_reviewer_performance_re~",
                        column: x => x.ReportId,
                        principalTable: "reviewer_performance_reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_code_insight_review_exposures_CodeInsightPullRequestId",
                table: "code_insight_review_exposures",
                column: "CodeInsightPullRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_code_insight_review_exposures_JobId_FilePath_ModelId_Logica~",
                table: "code_insight_review_exposures",
                columns: new[] { "JobId", "FilePath", "ModelId", "LogicalModelName", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reviewer_performance_daily_counts_ClientId_BucketDate",
                table: "reviewer_performance_daily_counts",
                columns: new[] { "ClientId", "BucketDate" });

            migrationBuilder.CreateIndex(
                name: "IX_reviewer_performance_daily_counts_CodeInsightPullRequestId_~",
                table: "reviewer_performance_daily_counts",
                columns: new[] { "CodeInsightPullRequestId", "BucketDate" });

            migrationBuilder.CreateIndex(
                name: "IX_reviewer_performance_daily_counts_CodeInsightPullRequestId~1",
                table: "reviewer_performance_daily_counts",
                columns: new[] { "CodeInsightPullRequestId", "CellKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reviewer_performance_report_clients_ClientId",
                table: "reviewer_performance_report_clients",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_reviewer_performance_reports_ExpiresAt",
                table: "reviewer_performance_reports",
                column: "ExpiresAt");
            migrationBuilder.Sql("""
                CREATE FUNCTION delete_reviewer_performance_reports_for_client() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    DELETE FROM reviewer_performance_reports r
                    WHERE EXISTS (SELECT 1 FROM reviewer_performance_report_clients m WHERE m."ReportId" = r."Id" AND m."ClientId" = OLD.id);
                    RETURN OLD;
                END;
                $$;
                CREATE TRIGGER delete_reviewer_performance_reports_before_client
                BEFORE DELETE ON clients FOR EACH ROW EXECUTE FUNCTION delete_reviewer_performance_reports_for_client();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS delete_reviewer_performance_reports_before_client ON clients; DROP FUNCTION IF EXISTS delete_reviewer_performance_reports_for_client();");
            migrationBuilder.DropTable(
                name: "code_insight_review_exposures");

            migrationBuilder.DropTable(
                name: "reviewer_performance_daily_counts");

            migrationBuilder.DropTable(
                name: "reviewer_performance_report_clients");

            migrationBuilder.DropTable(
                name: "reviewer_performance_reports");

            migrationBuilder.DropColumn(
                name: "MissHarvestObservedAt",
                table: "code_insight_pull_requests");

            migrationBuilder.DropColumn(
                name: "MissHarvestProviderScope",
                table: "code_insight_pull_requests");

            migrationBuilder.DropColumn(
                name: "MissHarvestSettled",
                table: "code_insight_pull_requests");

            migrationBuilder.DropColumn(
                name: "PerformanceProjectedAt",
                table: "code_insight_pull_requests");

            migrationBuilder.DropColumn(
                name: "PerformanceProjectionVersion",
                table: "code_insight_pull_requests");

            migrationBuilder.DropColumn(
                name: "DimensionClassifierVersion",
                table: "code_insight_misses");

            migrationBuilder.DropColumn(
                name: "DimensionConfidence",
                table: "code_insight_misses");

            migrationBuilder.DropColumn(
                name: "FailedJudgementAttempts",
                table: "code_insight_misses");

            migrationBuilder.DropColumn(
                name: "JudgementFailed",
                table: "code_insight_misses");

            migrationBuilder.DropColumn(
                name: "ProviderScope",
                table: "code_insight_misses");

            migrationBuilder.DropColumn(
                name: "Qualifier",
                table: "code_insight_misses");

            migrationBuilder.DropColumn(
                name: "SourceFingerprint",
                table: "code_insight_misses");

            migrationBuilder.DropColumn(
                name: "TypeMembership",
                table: "code_insight_misses");

            migrationBuilder.DropColumn(
                name: "CurrentClassifierConfidence",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "CurrentClassifierVersion",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "CurrentCodeChange",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "CurrentDisposition",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "DuplicateOfPublicationId",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "DuplicateState",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "DuplicateVerificationConfidence",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "DuplicateVerificationSource",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "DuplicateVerifiedAt",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "MatchedProviderThreadId",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "NativeStatus",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "OutcomeJudgementAttempts",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "OutcomeObservedAt",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "PerformanceEvidenceUpdatedAt",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "ProviderScope",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "PublicationReason",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "PublicationState",
                table: "code_insight_findings");

            migrationBuilder.DropColumn(
                name: "NativeStatus",
                table: "code_insight_finding_dispositions");
        }
    }
}
