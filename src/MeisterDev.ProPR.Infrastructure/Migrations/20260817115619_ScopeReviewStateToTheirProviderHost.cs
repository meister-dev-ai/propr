using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Puts the provider host and project into the identity of the remaining per-pull-request review state:
    ///     handled threads, thread memory and posted findings.
    /// </summary>
    /// <remarks>
    ///     The same defect the scan records had. A repository identifier is unique only within the host that
    ///     issued it, so one client holding a GitLab project 4 and a Forgejo repository 4 shared rows for every
    ///     pull request number the two had in common: a thread already answered on one host counted as answered
    ///     on the other, a memory of one repository's conversation suppressed a finding in the other's, and a
    ///     posted finding on one made a genuine finding on the other look like a duplicate.
    ///     Two of the three attribute exactly, through the job each row was written by: handled threads carry
    ///     their pass, and posted findings carry their review job. Thread memory carries no job, so it is
    ///     attributed the way the scan records were — from the jobs sharing its client, repository and number,
    ///     the most recently submitted deciding where two hosts both have one. What cannot be placed is removed
    ///     rather than left holding a host of empty string, which would match no lookup ever again: a memory is
    ///     an aid to a later review, so losing one costs a suppression that will not fire, not correctness.
    /// </remarks>
    public partial class ScopeReviewStateToTheirProviderHost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_thread_pass_handled_threads_key",
                table: "thread_pass_handled_threads");

            migrationBuilder.DropIndex(
                name: "ix_thread_memory_records_client_pr_updated_at",
                table: "thread_memory_records");

            migrationBuilder.DropIndex(
                name: "uq_thread_memory_records_thread",
                table: "thread_memory_records");

            migrationBuilder.DropIndex(
                name: "ix_posted_finding_records_pull_request",
                table: "posted_finding_records");

            migrationBuilder.DropIndex(
                name: "uq_posted_finding_records_thread",
                table: "posted_finding_records");

            migrationBuilder.AddColumn<string>(
                name: "organization_url",
                table: "thread_pass_handled_threads",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "project_id",
                table: "thread_pass_handled_threads",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "organization_url",
                table: "thread_memory_records",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "project_id",
                table: "thread_memory_records",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "organization_url",
                table: "posted_finding_records",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "project_id",
                table: "posted_finding_records",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Handled threads attribute exactly: each carries the pass that wrote it.
            migrationBuilder.Sql(
                """
                UPDATE thread_pass_handled_threads AS t
                SET organization_url = j.organization_url,
                    project_id = j.project_id
                FROM thread_pass_jobs AS j
                WHERE t.thread_pass_job_id = j.id;
                """);

            // Posted findings attribute exactly too: each carries the review job that posted it.
            migrationBuilder.Sql(
                """
                UPDATE posted_finding_records AS p
                SET organization_url = j.organization_url,
                    project_id = j.project_id
                FROM review_jobs AS j
                WHERE p.review_job_id = j.id;
                """);

            // Thread memory has no such link, so the jobs that ran against the same repository and number
            // decide, most recent first.
            migrationBuilder.Sql(
                """
                UPDATE thread_memory_records AS m
                SET organization_url = j.organization_url,
                    project_id = j.project_id
                FROM (
                    SELECT DISTINCT ON (client_id, repository_id, pull_request_id)
                           client_id, repository_id, pull_request_id, organization_url, project_id
                    FROM review_jobs
                    ORDER BY client_id, repository_id, pull_request_id, submitted_at DESC
                ) AS j
                WHERE m.client_id = j.client_id
                  AND m.repository_id = j.repository_id
                  AND m.pull_request_id = j.pull_request_id;
                """);

            // What no job accounts for cannot be placed, and an unplaceable row matches nothing from here on.
            migrationBuilder.Sql(
                """
                DELETE FROM thread_pass_handled_threads WHERE organization_url = '';
                DELETE FROM posted_finding_records WHERE organization_url = '';
                DELETE FROM thread_memory_records WHERE organization_url = '';
                """);

            migrationBuilder.CreateIndex(
                name: "uq_thread_pass_handled_threads_key",
                table: "thread_pass_handled_threads",
                columns: new[] { "client_id", "organization_url", "project_id", "repository_id", "pull_request_id", "thread_id", "observed_reply_count", "revision_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_thread_memory_records_client_pr_updated_at",
                table: "thread_memory_records",
                columns: new[] { "client_id", "organization_url", "project_id", "repository_id", "pull_request_id", "updated_at" },
                descending: new[] { false, false, false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "uq_thread_memory_records_thread",
                table: "thread_memory_records",
                columns: new[] { "client_id", "organization_url", "project_id", "repository_id", "thread_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_posted_finding_records_pull_request",
                table: "posted_finding_records",
                columns: new[] { "client_id", "organization_url", "project_id", "repository_id", "pull_request_id" });

            migrationBuilder.CreateIndex(
                name: "uq_posted_finding_records_thread",
                table: "posted_finding_records",
                columns: new[] { "client_id", "organization_url", "project_id", "repository_id", "pull_request_id", "provider_thread_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_thread_pass_handled_threads_key",
                table: "thread_pass_handled_threads");

            migrationBuilder.DropIndex(
                name: "ix_thread_memory_records_client_pr_updated_at",
                table: "thread_memory_records");

            migrationBuilder.DropIndex(
                name: "uq_thread_memory_records_thread",
                table: "thread_memory_records");

            migrationBuilder.DropIndex(
                name: "ix_posted_finding_records_pull_request",
                table: "posted_finding_records");

            migrationBuilder.DropIndex(
                name: "uq_posted_finding_records_thread",
                table: "posted_finding_records");

            migrationBuilder.DropColumn(
                name: "organization_url",
                table: "thread_pass_handled_threads");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "thread_pass_handled_threads");

            migrationBuilder.DropColumn(
                name: "organization_url",
                table: "thread_memory_records");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "thread_memory_records");

            migrationBuilder.DropColumn(
                name: "organization_url",
                table: "posted_finding_records");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "posted_finding_records");

            migrationBuilder.CreateIndex(
                name: "uq_thread_pass_handled_threads_key",
                table: "thread_pass_handled_threads",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "thread_id", "observed_reply_count", "revision_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_thread_memory_records_client_pr_updated_at",
                table: "thread_memory_records",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "updated_at" },
                descending: new[] { false, false, false, true });

            migrationBuilder.CreateIndex(
                name: "uq_thread_memory_records_thread",
                table: "thread_memory_records",
                columns: new[] { "client_id", "repository_id", "thread_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_posted_finding_records_pull_request",
                table: "posted_finding_records",
                columns: new[] { "client_id", "repository_id", "pull_request_id" });

            migrationBuilder.CreateIndex(
                name: "uq_posted_finding_records_thread",
                table: "posted_finding_records",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "provider_thread_id" },
                unique: true);
        }
    }
}
