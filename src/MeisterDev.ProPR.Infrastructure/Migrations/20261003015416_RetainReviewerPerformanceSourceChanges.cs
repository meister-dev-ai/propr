using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RetainReviewerPerformanceSourceChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DimensionJudgementFailed",
                table: "code_insight_misses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FailedDimensionAttempts",
                table: "code_insight_misses",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeSourceFingerprint",
                table: "code_insight_findings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "code_insight_harvest_coverage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeInsightPullRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderScope = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AllHumanThreadsResolved = table.Column<bool>(type: "boolean", nullable: false),
                    EnumerationComplete = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_harvest_coverage", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_insight_harvest_coverage_code_insight_pull_requests_Co~",
                        column: x => x.CodeInsightPullRequestId,
                        principalTable: "code_insight_pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "code_insight_performance_dirty",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeInsightPullRequestId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_performance_dirty", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_insight_performance_dirty_code_insight_pull_requests_C~",
                        column: x => x.CodeInsightPullRequestId,
                        principalTable: "code_insight_pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_code_insight_harvest_coverage_CodeInsightPullRequestId_Prov~",
                table: "code_insight_harvest_coverage",
                columns: new[] { "CodeInsightPullRequestId", "ProviderScope" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_insight_performance_dirty_CodeInsightPullRequestId",
                table: "code_insight_performance_dirty",
                column: "CodeInsightPullRequestId");

            migrationBuilder.Sql("""
                INSERT INTO code_insight_harvest_coverage ("Id", "CodeInsightPullRequestId", "ProviderScope", "ObservedAt", "AllHumanThreadsResolved", "EnumerationComplete")
                SELECT gen_random_uuid(), id, "MissHarvestProviderScope", "MissHarvestObservedAt", "MissHarvestSettled", true
                FROM code_insight_pull_requests WHERE "MissHarvestObservedAt" IS NOT NULL;

                CREATE FUNCTION mark_reviewer_performance_dirty() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE source_row jsonb; source_id uuid; aggregate_id uuid;
                BEGIN
                    IF TG_TABLE_NAME = 'code_insight_findings' AND TG_OP = 'UPDATE'
                        AND (to_jsonb(OLD) - 'OutcomeObservedAt') IS NOT DISTINCT FROM (to_jsonb(NEW) - 'OutcomeObservedAt') THEN
                        RETURN NULL;
                    END IF;
                    source_row := CASE WHEN TG_OP = 'DELETE' THEN to_jsonb(OLD) ELSE to_jsonb(NEW) END;
                    source_id := (source_row ->> TG_ARGV[0])::uuid;
                    IF TG_NARGS > 1 THEN
                        SELECT code_insight_pull_request_id INTO aggregate_id FROM code_insight_findings WHERE id = source_id;
                    ELSE
                        aggregate_id := source_id;
                    END IF;
                    INSERT INTO code_insight_performance_dirty ("Id", "CodeInsightPullRequestId")
                    SELECT gen_random_uuid(), id FROM code_insight_pull_requests WHERE id = aggregate_id;
                    RETURN NULL;
                END;
                $$;

                CREATE TRIGGER reviewer_performance_findings_dirty AFTER INSERT OR UPDATE OR DELETE ON code_insight_findings
                    FOR EACH ROW EXECUTE FUNCTION mark_reviewer_performance_dirty('code_insight_pull_request_id');
                CREATE TRIGGER reviewer_performance_tags_dirty AFTER INSERT OR UPDATE OR DELETE ON code_insight_finding_tags
                    FOR EACH ROW EXECUTE FUNCTION mark_reviewer_performance_dirty('code_insight_finding_id', 'finding');
                CREATE TRIGGER reviewer_performance_outcomes_dirty AFTER INSERT OR UPDATE OR DELETE ON code_insight_finding_dispositions
                    FOR EACH ROW EXECUTE FUNCTION mark_reviewer_performance_dirty('code_insight_finding_id', 'finding');
                CREATE TRIGGER reviewer_performance_misses_dirty AFTER INSERT OR UPDATE OR DELETE ON code_insight_misses
                    FOR EACH ROW EXECUTE FUNCTION mark_reviewer_performance_dirty('code_insight_pull_request_id');
                CREATE TRIGGER reviewer_performance_exposures_dirty AFTER INSERT OR UPDATE OR DELETE ON code_insight_review_exposures
                    FOR EACH ROW EXECUTE FUNCTION mark_reviewer_performance_dirty('CodeInsightPullRequestId');
                CREATE TRIGGER reviewer_performance_harvest_dirty AFTER INSERT OR UPDATE OR DELETE ON code_insight_harvest_coverage
                    FOR EACH ROW EXECUTE FUNCTION mark_reviewer_performance_dirty('CodeInsightPullRequestId');
                CREATE TRIGGER reviewer_performance_legacy_harvest_dirty
                    AFTER UPDATE OF "MissHarvestObservedAt", "MissHarvestSettled", "MissHarvestProviderScope" ON code_insight_pull_requests
                    FOR EACH ROW WHEN (OLD."MissHarvestObservedAt" IS DISTINCT FROM NEW."MissHarvestObservedAt"
                        OR OLD."MissHarvestSettled" IS DISTINCT FROM NEW."MissHarvestSettled"
                        OR OLD."MissHarvestProviderScope" IS DISTINCT FROM NEW."MissHarvestProviderScope")
                    EXECUTE FUNCTION mark_reviewer_performance_dirty('id');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER reviewer_performance_findings_dirty ON code_insight_findings;
                DROP TRIGGER reviewer_performance_tags_dirty ON code_insight_finding_tags;
                DROP TRIGGER reviewer_performance_outcomes_dirty ON code_insight_finding_dispositions;
                DROP TRIGGER reviewer_performance_misses_dirty ON code_insight_misses;
                DROP TRIGGER reviewer_performance_exposures_dirty ON code_insight_review_exposures;
                DROP TRIGGER reviewer_performance_harvest_dirty ON code_insight_harvest_coverage;
                DROP TRIGGER reviewer_performance_legacy_harvest_dirty ON code_insight_pull_requests;
                DROP FUNCTION mark_reviewer_performance_dirty();
                """);
            migrationBuilder.DropTable(
                name: "code_insight_harvest_coverage");

            migrationBuilder.DropTable(
                name: "code_insight_performance_dirty");

            migrationBuilder.DropColumn(
                name: "DimensionJudgementFailed",
                table: "code_insight_misses");

            migrationBuilder.DropColumn(
                name: "FailedDimensionAttempts",
                table: "code_insight_misses");

            migrationBuilder.DropColumn(
                name: "OutcomeSourceFingerprint",
                table: "code_insight_findings");
        }
    }
}
