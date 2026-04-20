// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookConfigurationsAndDeliveryHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_configurations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_type = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    public_path_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    organization_scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    organization_url = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    secret_ciphertext = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    enabled_events = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_configurations", x => x.id);
                    table.ForeignKey(
                        name: "FK_webhook_configurations_client_ado_organization_scopes_organ~",
                        column: x => x.organization_scope_id,
                        principalTable: "client_ado_organization_scopes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_webhook_configurations_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_delivery_log_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    webhook_configuration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    event_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    delivery_outcome = table.Column<int>(type: "integer", nullable: false),
                    http_status_code = table.Column<int>(type: "integer", nullable: false),
                    repository_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    pull_request_id = table.Column<int>(type: "integer", nullable: true),
                    source_branch = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    target_branch = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    action_summaries = table.Column<string>(type: "jsonb", nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_delivery_log_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_webhook_delivery_log_entries_webhook_configurations_webhook~",
                        column: x => x.webhook_configuration_id,
                        principalTable: "webhook_configurations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_repo_filters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    webhook_configuration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    canonical_source_ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    target_branch_patterns = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_repo_filters", x => x.id);
                    table.ForeignKey(
                        name: "FK_webhook_repo_filters_webhook_configurations_webhook_configu~",
                        column: x => x.webhook_configuration_id,
                        principalTable: "webhook_configurations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_configurations_active",
                table: "webhook_configurations",
                column: "is_active",
                filter: "is_active = true");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_configurations_client_id",
                table: "webhook_configurations",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_configurations_organization_scope_id",
                table: "webhook_configurations",
                column: "organization_scope_id");

            migrationBuilder.CreateIndex(
                name: "ux_webhook_configurations_public_path_key",
                table: "webhook_configurations",
                column: "public_path_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_log_entries_config_received_at",
                table: "webhook_delivery_log_entries",
                columns: new[] { "webhook_configuration_id", "received_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_repo_filters_config_source_ref",
                table: "webhook_repo_filters",
                columns: new[] { "webhook_configuration_id", "source_provider", "canonical_source_ref" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_delivery_log_entries");

            migrationBuilder.DropTable(
                name: "webhook_repo_filters");

            migrationBuilder.DropTable(
                name: "webhook_configurations");
        }
    }
}
