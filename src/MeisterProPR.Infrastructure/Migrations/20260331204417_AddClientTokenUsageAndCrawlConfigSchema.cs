// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientTokenUsageAndCrawlConfigSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_crawl_configurations_unique_config",
                table: "crawl_configurations");

            migrationBuilder.AddColumn<string>(
                name: "branch_filter",
                table: "crawl_configurations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "repository_id",
                table: "crawl_configurations",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "client_token_usage_samples",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_token_usage_samples", x => x.id);
                    table.ForeignKey(
                        name: "FK_client_token_usage_samples_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_client_token_usage_samples_unique",
                table: "client_token_usage_samples",
                columns: new[] { "client_id", "model_id", "date" },
                unique: true);

            // 5-field functional unique index on crawl_configurations using COALESCE for nullable columns.
            // Ensures (client_id, org, project, repo_or_empty, branch_or_empty) is unique.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ix_crawl_configurations_unique_config_v2 " +
                "ON crawl_configurations (client_id, organization_url, project_id, " +
                "COALESCE(repository_id, ''), COALESCE(branch_filter, ''));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_token_usage_samples");

            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_crawl_configurations_unique_config_v2;");

            migrationBuilder.DropColumn(
                name: "branch_filter",
                table: "crawl_configurations");

            migrationBuilder.DropColumn(
                name: "repository_id",
                table: "crawl_configurations");

            migrationBuilder.CreateIndex(
                name: "ix_crawl_configurations_unique_config",
                table: "crawl_configurations",
                columns: new[] { "client_id", "organization_url", "project_id" },
                unique: true);
        }
    }
}
