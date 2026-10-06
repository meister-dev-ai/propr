using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OrderReviewerPerformanceMissObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludedAsOwnFinding",
                table: "code_insight_misses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SourceObservedAt",
                table: "code_insight_misses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(DirtyFunction(includeMissWatermark: true));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DirtyFunction(includeMissWatermark: false));
            migrationBuilder.DropColumn(
                name: "ExcludedAsOwnFinding",
                table: "code_insight_misses");

            migrationBuilder.DropColumn(
                name: "SourceObservedAt",
                table: "code_insight_misses");
        }

        private static string DirtyFunction(bool includeMissWatermark) => $$"""
            CREATE OR REPLACE FUNCTION mark_reviewer_performance_dirty() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE source_row jsonb; source_id uuid; aggregate_id uuid;
            BEGIN
                IF TG_TABLE_NAME = 'code_insight_findings' AND TG_OP = 'UPDATE'
                    AND (to_jsonb(OLD) - 'OutcomeObservedAt') IS NOT DISTINCT FROM (to_jsonb(NEW) - 'OutcomeObservedAt') THEN
                    RETURN NULL;
                END IF;
                {{(includeMissWatermark ? """
                IF TG_TABLE_NAME = 'code_insight_misses' AND TG_OP = 'UPDATE'
                    AND (to_jsonb(OLD) - 'SourceObservedAt') IS NOT DISTINCT FROM (to_jsonb(NEW) - 'SourceObservedAt') THEN
                    RETURN NULL;
                END IF;
                """ : string.Empty)}}
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
            """;
    }
}
