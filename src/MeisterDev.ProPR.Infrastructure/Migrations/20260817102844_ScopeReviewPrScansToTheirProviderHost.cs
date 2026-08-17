using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Puts the provider host and project into the identity of a pull request's scan record.
    /// </summary>
    /// <remarks>
    ///     A repository identifier is unique only within the host that issued it, and providers hand out small
    ///     integers freely. One client holding a GitLab project 4 and a Forgejo repository 4 therefore shared a
    ///     single row per pull request number and read each other's watermarks: a months-old Forgejo review of
    ///     its pull request 7 made a newly opened GitLab merge request 7 look like a pull request already
    ///     reviewed at another revision, and an installation that reviews only the first increment declined it.
    ///     Existing rows are attributed from the review jobs that share their client, repository and number,
    ///     taking the most recently submitted job's host where more than one exists. A row no job accounts for
    ///     cannot be attributed at all, and is removed rather than left holding a host of empty string: such a
    ///     row matches no lookup ever again, so keeping it would accumulate dead weight beside the live row that
    ///     replaces it. Its jobs were pruned, which makes it a pull request nobody is crawling; the cost if one
    ///     still is, is a single re-read.
    /// </remarks>
    public partial class ScopeReviewPrScansToTheirProviderHost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_review_pr_scans_pr",
                table: "review_pr_scans");

            migrationBuilder.AddColumn<string>(
                name: "organization_url",
                table: "review_pr_scans",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "project_id",
                table: "review_pr_scans",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Attributed from the jobs that ran against the same repository and number. The most recent job
            // decides where two hosts both have one, which is the pair a fresh delivery would carry anyway.
            migrationBuilder.Sql(
                """
                UPDATE review_pr_scans AS s
                SET organization_url = j.organization_url,
                    project_id = j.project_id
                FROM (
                    SELECT DISTINCT ON (client_id, repository_id, pull_request_id)
                           client_id, repository_id, pull_request_id, organization_url, project_id
                    FROM review_jobs
                    ORDER BY client_id, repository_id, pull_request_id, submitted_at DESC
                ) AS j
                WHERE s.client_id = j.client_id
                  AND s.repository_id = j.repository_id
                  AND s.pull_request_id = j.pull_request_id;
                """);

            // What no job accounts for cannot be placed, and an unplaceable row matches nothing from here on.
            migrationBuilder.Sql(
                """
                DELETE FROM review_pr_scans WHERE organization_url = '';
                """);

            migrationBuilder.CreateIndex(
                name: "uq_review_pr_scans_pr",
                table: "review_pr_scans",
                columns: new[] { "client_id", "organization_url", "project_id", "repository_id", "pull_request_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_review_pr_scans_pr",
                table: "review_pr_scans");

            migrationBuilder.DropColumn(
                name: "organization_url",
                table: "review_pr_scans");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "review_pr_scans");

            migrationBuilder.CreateIndex(
                name: "uq_review_pr_scans_pr",
                table: "review_pr_scans",
                columns: new[] { "client_id", "repository_id", "pull_request_id" },
                unique: true);
        }
    }
}
