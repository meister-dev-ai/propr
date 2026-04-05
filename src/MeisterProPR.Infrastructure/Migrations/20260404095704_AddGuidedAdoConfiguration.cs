// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGuidedAdoConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_crawl_repo_filters_config_repo",
                table: "crawl_repo_filters");

            migrationBuilder.AddColumn<string>(
                name: "canonical_source_ref",
                table: "crawl_repo_filters",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "crawl_repo_filters",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_provider",
                table: "crawl_repo_filters",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "organization_scope_id",
                table: "crawl_configurations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "procursor_source_scope_mode",
                table: "crawl_configurations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "canonical_source_provider",
                table: "procursor_knowledge_sources",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "canonical_source_value",
                table: "procursor_knowledge_sources",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "organization_scope_id",
                table: "procursor_knowledge_sources",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_display_name",
                table: "procursor_knowledge_sources",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "client_ado_organization_scopes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    verification_status = table.Column<int>(type: "integer", nullable: false),
                    last_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_verification_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_ado_organization_scopes", x => x.id);
                    table.ForeignKey(
                        name: "FK_client_ado_organization_scopes_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "crawl_configuration_procursor_sources",
                columns: table => new
                {
                    crawl_configuration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    procursor_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crawl_configuration_procursor_sources", x => new { x.crawl_configuration_id, x.procursor_source_id });
                    table.ForeignKey(
                        name: "FK_crawl_configuration_procursor_sources_crawl_configurations_~",
                        column: x => x.crawl_configuration_id,
                        principalTable: "crawl_configurations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_crawl_configuration_procursor_sources_procursor_knowledge_s~",
                        column: x => x.procursor_source_id,
                        principalTable: "procursor_knowledge_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "review_job_procursor_source_scopes",
                columns: table => new
                {
                    review_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    procursor_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_job_procursor_source_scopes", x => new { x.review_job_id, x.procursor_source_id });
                    table.ForeignKey(
                        name: "FK_review_job_procursor_source_scopes_procursor_knowledge_sour~",
                        column: x => x.procursor_source_id,
                        principalTable: "procursor_knowledge_sources",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_review_job_procursor_source_scopes_review_jobs_review_job_id",
                        column: x => x.review_job_id,
                        principalTable: "review_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_crawl_repo_filters_config_source_ref",
                table: "crawl_repo_filters",
                columns: new[] { "crawl_configuration_id", "source_provider", "canonical_source_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_crawl_configurations_organization_scope_id",
                table: "crawl_configurations",
                column: "organization_scope_id");

            migrationBuilder.CreateIndex(
                name: "ix_client_ado_organization_scopes_client_url",
                table: "client_ado_organization_scopes",
                columns: new[] { "client_id", "organization_url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_crawl_configuration_procursor_sources_source_id",
                table: "crawl_configuration_procursor_sources",
                column: "procursor_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_procursor_knowledge_sources_organization_scope_id",
                table: "procursor_knowledge_sources",
                column: "organization_scope_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_job_procursor_source_scopes_source_id",
                table: "review_job_procursor_source_scopes",
                column: "procursor_source_id");

            migrationBuilder.AddForeignKey(
                name: "FK_crawl_configurations_client_ado_organization_scopes_organiz~",
                table: "crawl_configurations",
                column: "organization_scope_id",
                principalTable: "client_ado_organization_scopes",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_procursor_knowledge_sources_client_ado_org_scopes_org_scope_id",
                table: "procursor_knowledge_sources",
                column: "organization_scope_id",
                principalTable: "client_ado_organization_scopes",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_crawl_configurations_client_ado_organization_scopes_organiz~",
                table: "crawl_configurations");

            migrationBuilder.DropForeignKey(
                name: "FK_procursor_knowledge_sources_client_ado_org_scopes_org_scope_id",
                table: "procursor_knowledge_sources");

            migrationBuilder.DropTable(
                name: "client_ado_organization_scopes");

            migrationBuilder.DropTable(
                name: "crawl_configuration_procursor_sources");

            migrationBuilder.DropTable(
                name: "review_job_procursor_source_scopes");

            migrationBuilder.DropIndex(
                name: "ix_crawl_repo_filters_config_source_ref",
                table: "crawl_repo_filters");

            migrationBuilder.DropIndex(
                name: "ix_crawl_configurations_organization_scope_id",
                table: "crawl_configurations");

            migrationBuilder.DropIndex(
                name: "ix_procursor_knowledge_sources_organization_scope_id",
                table: "procursor_knowledge_sources");

            migrationBuilder.DropColumn(
                name: "canonical_source_ref",
                table: "crawl_repo_filters");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "crawl_repo_filters");

            migrationBuilder.DropColumn(
                name: "source_provider",
                table: "crawl_repo_filters");

            migrationBuilder.DropColumn(
                name: "organization_scope_id",
                table: "crawl_configurations");

            migrationBuilder.DropColumn(
                name: "procursor_source_scope_mode",
                table: "crawl_configurations");

            migrationBuilder.DropColumn(
                name: "canonical_source_provider",
                table: "procursor_knowledge_sources");

            migrationBuilder.DropColumn(
                name: "canonical_source_value",
                table: "procursor_knowledge_sources");

            migrationBuilder.DropColumn(
                name: "organization_scope_id",
                table: "procursor_knowledge_sources");

            migrationBuilder.DropColumn(
                name: "source_display_name",
                table: "procursor_knowledge_sources");

            migrationBuilder.CreateIndex(
                name: "ix_crawl_repo_filters_config_repo",
                table: "crawl_repo_filters",
                columns: new[] { "crawl_configuration_id", "repository_name" },
                unique: true);
        }
    }
}
