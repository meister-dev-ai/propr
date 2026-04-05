// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clients", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "review_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    client_key = table.Column<string>(type: "text", nullable: true),
                    organization_url = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    repository_id = table.Column<string>(type: "text", nullable: false),
                    pull_request_id = table.Column<int>(type: "integer", nullable: false),
                    iteration_id = table.Column<int>(type: "integer", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processing_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "crawl_configurations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_url = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    reviewer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crawl_interval_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 60),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crawl_configurations", x => x.id);
                    table.ForeignKey(
                        name: "FK_crawl_configurations_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clients_key",
                table: "clients",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_crawl_configurations_active",
                table: "crawl_configurations",
                column: "is_active",
                filter: "is_active = true");

            migrationBuilder.CreateIndex(
                name: "ix_crawl_configurations_client_id",
                table: "crawl_configurations",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_jobs_client_key",
                table: "review_jobs",
                column: "client_key");

            migrationBuilder.CreateIndex(
                name: "ix_review_jobs_pr_identity",
                table: "review_jobs",
                columns: new[] { "organization_url", "project_id", "repository_id", "pull_request_id", "iteration_id" });

            migrationBuilder.CreateIndex(
                name: "ix_review_jobs_status",
                table: "review_jobs",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crawl_configurations");

            migrationBuilder.DropTable(
                name: "review_jobs");

            migrationBuilder.DropTable(
                name: "clients");
        }
    }
}
