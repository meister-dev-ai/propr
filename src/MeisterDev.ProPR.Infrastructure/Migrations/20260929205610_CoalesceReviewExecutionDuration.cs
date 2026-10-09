using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CoalesceReviewExecutionDuration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION propr_accumulate_review_execution_duration() RETURNS trigger AS $$
                BEGIN
                    IF OLD.status = 'Processing' AND NEW.status <> 'Processing'
                       AND OLD.processing_started_at IS NOT NULL THEN
                        NEW.execution_duration_ms := COALESCE(OLD.execution_duration_ms, 0) + GREATEST(0,
                            FLOOR(EXTRACT(EPOCH FROM (
                                LEAST(clock_timestamp(), COALESCE(OLD.lease_expires_at, clock_timestamp()))
                                - OLD.processing_started_at)) * 1000)::bigint);
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION propr_accumulate_review_execution_duration() RETURNS trigger AS $$
                BEGIN
                    IF OLD.status = 'Processing' AND NEW.status <> 'Processing'
                       AND OLD.processing_started_at IS NOT NULL THEN
                        NEW.execution_duration_ms := COALESCE(OLD.execution_duration_ms, 0) + GREATEST(0,
                            FLOOR(EXTRACT(EPOCH FROM (
                                LEAST(clock_timestamp(), COALESCE(OLD.lease_expires_at, clock_timestamp()))
                                - OLD.processing_started_at)) * 1000)::bigint);
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);
        }
    }
}
