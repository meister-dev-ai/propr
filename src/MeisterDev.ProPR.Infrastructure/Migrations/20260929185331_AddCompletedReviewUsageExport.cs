using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompletedReviewUsageExport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "execution_duration_ms",
                table: "review_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "usage_finalized_at",
                table: "review_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "completed_review_usage_snapshots",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    execution_duration_ms = table.Column<long>(type: "bigint", nullable: false),
                    ai_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    estimated_cost_usd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    cost_is_approximate = table.Column<bool>(type: "boolean", nullable: false),
                    finalized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_completed_review_usage_snapshots", x => x.sequence);
                });

            migrationBuilder.CreateIndex(
                name: "IX_completed_review_usage_snapshots_client_id_sequence",
                table: "completed_review_usage_snapshots",
                columns: new[] { "client_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_completed_review_usage_snapshots_job_id",
                table: "completed_review_usage_snapshots",
                column: "job_id",
                unique: true);

            migrationBuilder.Sql("""
                CREATE FUNCTION propr_accumulate_review_execution_duration() RETURNS trigger AS $$
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
                CREATE TRIGGER trg_review_execution_duration
                BEFORE UPDATE OF status ON review_jobs
                FOR EACH ROW EXECUTE FUNCTION propr_accumulate_review_execution_duration();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_review_execution_duration ON review_jobs; DROP FUNCTION IF EXISTS propr_accumulate_review_execution_duration();");
            migrationBuilder.DropTable(
                name: "completed_review_usage_snapshots");

            migrationBuilder.DropColumn(
                name: "execution_duration_ms",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "usage_finalized_at",
                table: "review_jobs");
        }
    }
}
