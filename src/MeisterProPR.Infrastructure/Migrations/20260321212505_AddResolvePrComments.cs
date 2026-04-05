// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResolvePrComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "comment_resolution_behavior",
                table: "clients",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "review_pr_scans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<string>(type: "text", nullable: false),
                    pull_request_id = table.Column<int>(type: "integer", nullable: false),
                    last_processed_commit_id = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_pr_scans", x => x.id);
                    table.ForeignKey(
                        name: "FK_review_pr_scans_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "review_pr_scan_threads",
                columns: table => new
                {
                    review_pr_scan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<int>(type: "integer", nullable: false),
                    last_seen_reply_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_pr_scan_threads", x => new { x.review_pr_scan_id, x.thread_id });
                    table.ForeignKey(
                        name: "FK_review_pr_scan_threads_review_pr_scans_review_pr_scan_id",
                        column: x => x.review_pr_scan_id,
                        principalTable: "review_pr_scans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_review_pr_scans_pr",
                table: "review_pr_scans",
                columns: new[] { "client_id", "repository_id", "pull_request_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_pr_scan_threads");

            migrationBuilder.DropTable(
                name: "review_pr_scans");

            migrationBuilder.DropColumn(
                name: "comment_resolution_behavior",
                table: "clients");
        }
    }
}
