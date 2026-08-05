using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ThreadPassIdempotencyAndClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_thread_pass_jobs_trigger",
                table: "thread_pass_jobs");

            migrationBuilder.DropIndex(
                name: "uq_thread_pass_handled_threads_key",
                table: "thread_pass_handled_threads");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "thread_pass_jobs",
                type: "timestamp with time zone",
                nullable: true);

            // Rows written before the revision was part of the key say nothing about which revision they were
            // recorded at, so they are left with a value no current revision matches. They then suppress
            // nothing, which is the outcome wanted: a finding those rows had made permanently unassessable
            // becomes assessable again on the next push.
            migrationBuilder.AddColumn<string>(
                name: "revision_key",
                table: "thread_pass_handled_threads",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            // The in-flight index cannot be created while a pull request already holds two, which the
            // read-then-insert claim it replaces allowed. The newest one keeps the pull request and the rest
            // are cancelled, which is the same outcome the claim should have produced at the time.
            migrationBuilder.Sql(
                """
                UPDATE thread_pass_jobs AS stale
                SET status = 'Cancelled',
                    completed_at = NOW()
                WHERE stale.status IN ('Pending', 'Processing')
                  AND EXISTS (
                      SELECT 1
                      FROM thread_pass_jobs AS winner
                      WHERE winner.client_id = stale.client_id
                        AND winner.repository_id = stale.repository_id
                        AND winner.pull_request_id = stale.pull_request_id
                        AND winner.status IN ('Pending', 'Processing')
                        AND (winner.created_at, winner.id) > (stale.created_at, stale.id));
                """);

            migrationBuilder.CreateIndex(
                name: "uq_thread_pass_jobs_in_flight",
                table: "thread_pass_jobs",
                columns: new[] { "client_id", "repository_id", "pull_request_id" },
                unique: true,
                filter: "status IN ('Pending', 'Processing')");

            migrationBuilder.CreateIndex(
                name: "uq_thread_pass_jobs_trigger",
                table: "thread_pass_jobs",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "trigger_key" },
                unique: true,
                filter: "status <> 'Skipped'");

            migrationBuilder.CreateIndex(
                name: "uq_thread_pass_handled_threads_key",
                table: "thread_pass_handled_threads",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "thread_id", "observed_reply_count", "revision_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_thread_pass_jobs_in_flight",
                table: "thread_pass_jobs");

            migrationBuilder.DropIndex(
                name: "uq_thread_pass_jobs_trigger",
                table: "thread_pass_jobs");

            migrationBuilder.DropIndex(
                name: "uq_thread_pass_handled_threads_key",
                table: "thread_pass_handled_threads");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "thread_pass_jobs");

            migrationBuilder.DropColumn(
                name: "revision_key",
                table: "thread_pass_handled_threads");

            migrationBuilder.CreateIndex(
                name: "uq_thread_pass_jobs_trigger",
                table: "thread_pass_jobs",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "trigger_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_thread_pass_handled_threads_key",
                table: "thread_pass_handled_threads",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "thread_id", "observed_reply_count" },
                unique: true);
        }
    }
}
