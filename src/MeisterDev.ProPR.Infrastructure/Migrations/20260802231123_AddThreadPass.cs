using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddThreadPass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_thread_pass_revision_key",
                table: "review_pr_scans",
                type: "text",
                nullable: false,
                defaultValue: "");

            // An absent thread watermark differs from every current revision, so without this every open pull
            // request would be due a thread pass on the first tick after deployment, all at once. Seeding it
            // from the revision the file pass last recorded means only pull requests that genuinely moved, or
            // that carry a new non-reviewer comment, are picked up.
            migrationBuilder.Sql(
                "UPDATE review_pr_scans SET last_thread_pass_revision_key = last_processed_commit_id;");

            migrationBuilder.CreateTable(
                name: "thread_pass_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_url = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    repository_id = table.Column<string>(type: "text", nullable: false),
                    pull_request_id = table.Column<int>(type: "integer", nullable: false),
                    iteration_id = table.Column<int>(type: "integer", nullable: false),
                    revision_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    trigger_key = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    host_base_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    repository_owner_or_namespace = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    repository_project_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    code_review_platform_kind = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    external_code_review_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processing_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_thread_pass_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_thread_pass_jobs_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "thread_pass_handled_threads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_pass_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<string>(type: "text", nullable: false),
                    pull_request_id = table.Column<int>(type: "integer", nullable: false),
                    thread_id = table.Column<long>(type: "bigint", nullable: false),
                    observed_reply_count = table.Column<int>(type: "integer", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_thread_pass_handled_threads", x => x.id);
                    table.ForeignKey(
                        name: "FK_thread_pass_handled_threads_thread_pass_jobs_thread_pass_jo~",
                        column: x => x.thread_pass_job_id,
                        principalTable: "thread_pass_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_thread_pass_handled_threads_thread_pass_job_id",
                table: "thread_pass_handled_threads",
                column: "thread_pass_job_id");

            migrationBuilder.CreateIndex(
                name: "uq_thread_pass_handled_threads_key",
                table: "thread_pass_handled_threads",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "thread_id", "observed_reply_count" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_thread_pass_jobs_pr_status",
                table: "thread_pass_jobs",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_thread_pass_jobs_status",
                table: "thread_pass_jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "uq_thread_pass_jobs_trigger",
                table: "thread_pass_jobs",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "trigger_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "thread_pass_handled_threads");

            migrationBuilder.DropTable(
                name: "thread_pass_jobs");

            migrationBuilder.DropColumn(
                name: "last_thread_pass_revision_key",
                table: "review_pr_scans");
        }
    }
}
