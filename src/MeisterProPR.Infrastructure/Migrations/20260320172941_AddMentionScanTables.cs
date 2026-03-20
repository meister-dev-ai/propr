using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMentionScanTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mention_pr_scans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    crawl_configuration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<string>(type: "text", nullable: false),
                    pull_request_id = table.Column<int>(type: "integer", nullable: false),
                    last_comment_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mention_pr_scans", x => x.id);
                    table.ForeignKey(
                        name: "FK_mention_pr_scans_crawl_configurations_crawl_configuration_id",
                        column: x => x.crawl_configuration_id,
                        principalTable: "crawl_configurations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mention_project_scans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    crawl_configuration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_scanned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mention_project_scans", x => x.id);
                    table.ForeignKey(
                        name: "FK_mention_project_scans_crawl_configurations_crawl_configurat~",
                        column: x => x.crawl_configuration_id,
                        principalTable: "crawl_configurations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mention_reply_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_url = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    repository_id = table.Column<string>(type: "text", nullable: false),
                    pull_request_id = table.Column<int>(type: "integer", nullable: false),
                    thread_id = table.Column<int>(type: "integer", nullable: false),
                    comment_id = table.Column<int>(type: "integer", nullable: false),
                    mention_text = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processing_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mention_reply_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_mention_reply_jobs_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "uq_mention_pr_scans_pr",
                table: "mention_pr_scans",
                columns: new[] { "crawl_configuration_id", "repository_id", "pull_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_mention_project_scans_config",
                table: "mention_project_scans",
                column: "crawl_configuration_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mention_reply_jobs_client_id",
                table: "mention_reply_jobs",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_mention_reply_jobs_status",
                table: "mention_reply_jobs",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "uq_mention_reply_jobs_mention",
                table: "mention_reply_jobs",
                columns: new[] { "client_id", "pull_request_id", "thread_id", "comment_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mention_pr_scans");

            migrationBuilder.DropTable(
                name: "mention_project_scans");

            migrationBuilder.DropTable(
                name: "mention_reply_jobs");
        }
    }
}
