// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiConnectionsCrawlFiltersAndJobPrContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ai_connection_id",
                table: "review_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_model",
                table: "review_jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pr_repository_name",
                table: "review_jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pr_source_branch",
                table: "review_jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pr_target_branch",
                table: "review_jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pr_title",
                table: "review_jobs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    endpoint_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    models = table.Column<string>(type: "jsonb", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    active_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    api_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_connections", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_connections_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "crawl_repo_filters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    crawl_configuration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    target_branch_patterns = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crawl_repo_filters", x => x.id);
                    table.ForeignKey(
                        name: "FK_crawl_repo_filters_crawl_configurations_crawl_configuration~",
                        column: x => x.crawl_configuration_id,
                        principalTable: "crawl_configurations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_connections_client_id_active",
                table: "ai_connections",
                column: "client_id",
                unique: true,
                filter: "is_active = true");

            migrationBuilder.CreateIndex(
                name: "ix_ai_connections_client_id_display_name",
                table: "ai_connections",
                columns: new[] { "client_id", "display_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_crawl_repo_filters_config_repo",
                table: "crawl_repo_filters",
                columns: new[] { "crawl_configuration_id", "repository_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_connections");

            migrationBuilder.DropTable(
                name: "crawl_repo_filters");

            migrationBuilder.DropColumn(
                name: "ai_connection_id",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "ai_model",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "pr_repository_name",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "pr_source_branch",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "pr_target_branch",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "pr_title",
                table: "review_jobs");
        }
    }
}
