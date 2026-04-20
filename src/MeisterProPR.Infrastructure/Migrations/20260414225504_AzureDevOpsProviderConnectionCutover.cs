// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AzureDevOpsProviderConnectionCutover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_crawl_configurations_client_ado_organization_scopes_organiz~",
                table: "crawl_configurations");

            migrationBuilder.DropForeignKey(
                name: "FK_procursor_knowledge_sources_client_ado_org_scopes_org_scope_id",
                table: "procursor_knowledge_sources");

            migrationBuilder.DropForeignKey(
                name: "FK_webhook_configurations_client_ado_organization_scopes_organ~",
                table: "webhook_configurations");

            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE IF NOT EXISTS ado_scope_backfill_map (
                    old_scope_id uuid PRIMARY KEY,
                    target_scope_id uuid NOT NULL,
                    client_id uuid NOT NULL,
                    connection_id uuid NOT NULL,
                    scope_type character varying(64) NOT NULL,
                    external_scope_id character varying(256) NOT NULL,
                    scope_path character varying(512) NOT NULL,
                    display_name character varying(256) NOT NULL,
                    verification_status character varying(64) NOT NULL,
                    is_enabled boolean NOT NULL,
                    last_verified_at timestamp with time zone,
                    last_verification_error text,
                    created_at timestamp with time zone NOT NULL,
                    updated_at timestamp with time zone NOT NULL
                ) ON COMMIT DROP;

                INSERT INTO ado_scope_backfill_map (
                    old_scope_id,
                    target_scope_id,
                    client_id,
                    connection_id,
                    scope_type,
                    external_scope_id,
                    scope_path,
                    display_name,
                    verification_status,
                    is_enabled,
                    last_verified_at,
                    last_verification_error,
                    created_at,
                    updated_at)
                SELECT
                    old_scope.id,
                    COALESCE(existing_scope.id, old_scope.id) AS target_scope_id,
                    old_scope.client_id,
                    connection.id,
                    'organization',
                    trim(trailing '/' from old_scope.organization_url),
                    trim(trailing '/' from old_scope.organization_url),
                    COALESCE(NULLIF(btrim(old_scope.display_name), ''), trim(trailing '/' from old_scope.organization_url)),
                    CASE old_scope.verification_status
                        WHEN 1 THEN 'verified'
                        WHEN 2 THEN 'failed'
                        WHEN 3 THEN 'failed'
                        WHEN 4 THEN 'stale'
                        ELSE 'unknown'
                    END,
                    old_scope.is_enabled,
                    old_scope.last_verified_at,
                    old_scope.last_verification_error,
                    old_scope.created_at,
                    old_scope.updated_at
                FROM client_ado_organization_scopes old_scope
                INNER JOIN client_scm_connections connection
                    ON connection.client_id = old_scope.client_id
                   AND connection.provider = 0
                   AND lower(connection.host_base_url) = COALESCE(
                        substring(lower(trim(trailing '/' from old_scope.organization_url)) from '^(https?://[^/]+)'),
                        lower(trim(trailing '/' from old_scope.organization_url)))
                LEFT JOIN client_scm_scopes existing_scope
                    ON existing_scope.client_id = old_scope.client_id
                   AND existing_scope.connection_id = connection.id
                   AND lower(existing_scope.scope_type) = 'organization'
                   AND lower(existing_scope.external_scope_id) = lower(trim(trailing '/' from old_scope.organization_url));

                UPDATE procursor_knowledge_sources target
                SET organization_scope_id = map.target_scope_id
                FROM ado_scope_backfill_map map
                WHERE target.organization_scope_id = map.old_scope_id;

                UPDATE crawl_configurations target
                SET organization_scope_id = map.target_scope_id
                FROM ado_scope_backfill_map map
                WHERE target.organization_scope_id = map.old_scope_id;

                UPDATE webhook_configurations target
                SET organization_scope_id = map.target_scope_id
                FROM ado_scope_backfill_map map
                WHERE target.organization_scope_id = map.old_scope_id;

                INSERT INTO client_scm_scopes (
                    id,
                    client_id,
                    connection_id,
                    scope_type,
                    external_scope_id,
                    scope_path,
                    display_name,
                    verification_status,
                    is_enabled,
                    last_verified_at,
                    last_verification_error,
                    created_at,
                    updated_at)
                SELECT
                    old_scope_id,
                    client_id,
                    connection_id,
                    scope_type,
                    external_scope_id,
                    scope_path,
                    display_name,
                    verification_status,
                    is_enabled,
                    last_verified_at,
                    last_verification_error,
                    created_at,
                    updated_at
                FROM ado_scope_backfill_map
                WHERE target_scope_id = old_scope_id
                ON CONFLICT (id) DO NOTHING;

                                UPDATE procursor_knowledge_sources target
                                SET organization_scope_id = (
                                        SELECT scope.id
                                        FROM client_scm_scopes scope
                                        WHERE scope.client_id = target.client_id
                                            AND lower(scope.scope_type) = 'organization'
                                            AND lower(trim(trailing '/' from scope.scope_path)) = lower(trim(trailing '/' from target.organization_url))
                                        ORDER BY scope.is_enabled DESC, scope.updated_at DESC, scope.created_at DESC, scope.id
                                        LIMIT 1)
                                WHERE target.organization_scope_id IS NOT NULL
                                    AND NOT EXISTS (
                                            SELECT 1
                                            FROM client_scm_scopes scope
                                            WHERE scope.id = target.organization_scope_id);

                                UPDATE crawl_configurations target
                                SET organization_scope_id = (
                                        SELECT scope.id
                                        FROM client_scm_scopes scope
                                        WHERE scope.client_id = target.client_id
                                            AND lower(scope.scope_type) = 'organization'
                                            AND lower(trim(trailing '/' from scope.scope_path)) = lower(trim(trailing '/' from target.organization_url))
                                        ORDER BY scope.is_enabled DESC, scope.updated_at DESC, scope.created_at DESC, scope.id
                                        LIMIT 1)
                                WHERE target.organization_scope_id IS NOT NULL
                                    AND NOT EXISTS (
                                            SELECT 1
                                            FROM client_scm_scopes scope
                                            WHERE scope.id = target.organization_scope_id);

                                UPDATE webhook_configurations target
                                SET organization_scope_id = (
                                        SELECT scope.id
                                        FROM client_scm_scopes scope
                                        WHERE scope.client_id = target.client_id
                                            AND lower(scope.scope_type) = 'organization'
                                            AND lower(trim(trailing '/' from scope.scope_path)) = lower(trim(trailing '/' from target.organization_url))
                                        ORDER BY scope.is_enabled DESC, scope.updated_at DESC, scope.created_at DESC, scope.id
                                        LIMIT 1)
                                WHERE target.organization_scope_id IS NOT NULL
                                    AND NOT EXISTS (
                                            SELECT 1
                                            FROM client_scm_scopes scope
                                            WHERE scope.id = target.organization_scope_id);

                                UPDATE procursor_knowledge_sources target
                                SET organization_scope_id = NULL
                                WHERE target.organization_scope_id IS NOT NULL
                                    AND NOT EXISTS (
                                            SELECT 1
                                            FROM client_scm_scopes scope
                                            WHERE scope.id = target.organization_scope_id);

                                UPDATE crawl_configurations target
                                SET organization_scope_id = NULL
                                WHERE target.organization_scope_id IS NOT NULL
                                    AND NOT EXISTS (
                                            SELECT 1
                                            FROM client_scm_scopes scope
                                            WHERE scope.id = target.organization_scope_id);

                                UPDATE webhook_configurations target
                                SET organization_scope_id = NULL
                                WHERE target.organization_scope_id IS NOT NULL
                                    AND NOT EXISTS (
                                            SELECT 1
                                            FROM client_scm_scopes scope
                                            WHERE scope.id = target.organization_scope_id);
                """);

            migrationBuilder.DropTable(
                name: "client_ado_organization_scopes");

            migrationBuilder.DropColumn(
                name: "ado_client_id",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "ado_client_secret",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "ado_tenant_id",
                table: "clients");

            migrationBuilder.AddColumn<string>(
                name: "oauth_client_id",
                table: "client_scm_connections",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "oauth_tenant_id",
                table: "client_scm_connections",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_crawl_configurations_client_scm_scopes_organization_scope_id",
                table: "crawl_configurations",
                column: "organization_scope_id",
                principalTable: "client_scm_scopes",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_procursor_knowledge_sources_client_scm_scopes_organization_~",
                table: "procursor_knowledge_sources",
                column: "organization_scope_id",
                principalTable: "client_scm_scopes",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_webhook_configurations_client_scm_scopes_organization_scope~",
                table: "webhook_configurations",
                column: "organization_scope_id",
                principalTable: "client_scm_scopes",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_crawl_configurations_client_scm_scopes_organization_scope_id",
                table: "crawl_configurations");

            migrationBuilder.DropForeignKey(
                name: "FK_procursor_knowledge_sources_client_scm_scopes_organization_~",
                table: "procursor_knowledge_sources");

            migrationBuilder.DropForeignKey(
                name: "FK_webhook_configurations_client_scm_scopes_organization_scope~",
                table: "webhook_configurations");

            migrationBuilder.DropColumn(
                name: "oauth_client_id",
                table: "client_scm_connections");

            migrationBuilder.DropColumn(
                name: "oauth_tenant_id",
                table: "client_scm_connections");

            migrationBuilder.AddColumn<string>(
                name: "ado_client_id",
                table: "clients",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ado_client_secret",
                table: "clients",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ado_tenant_id",
                table: "clients",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "client_ado_organization_scopes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    last_verification_error = table.Column<string>(type: "text", nullable: true),
                    last_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    organization_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    verification_status = table.Column<int>(type: "integer", nullable: false)
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

            migrationBuilder.CreateIndex(
                name: "ix_client_ado_organization_scopes_client_url",
                table: "client_ado_organization_scopes",
                columns: new[] { "client_id", "organization_url" },
                unique: true);

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

            migrationBuilder.AddForeignKey(
                name: "FK_webhook_configurations_client_ado_organization_scopes_organ~",
                table: "webhook_configurations",
                column: "organization_scope_id",
                principalTable: "client_ado_organization_scopes",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
