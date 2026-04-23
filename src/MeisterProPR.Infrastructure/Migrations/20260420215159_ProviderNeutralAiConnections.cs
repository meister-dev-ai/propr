// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderNeutralAiConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_connection_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider_kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    base_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    auth_mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    protected_secret = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    default_headers = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                    default_query_params = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                    discovery_mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_connection_profiles", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_connection_profiles_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_configured_models",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    remote_model_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    operation_kinds = table.Column<string>(type: "jsonb", nullable: false),
                    supported_protocol_modes = table.Column<string>(type: "jsonb", nullable: false),
                    tokenizer_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    max_input_tokens = table.Column<int>(type: "integer", nullable: true),
                    embedding_dimensions = table.Column<int>(type: "integer", nullable: true),
                    supports_structured_output = table.Column<bool>(type: "boolean", nullable: false),
                    supports_tool_use = table.Column<bool>(type: "boolean", nullable: false),
                    source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    input_cost_per_1m_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    output_cost_per_1m_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_configured_models", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_configured_models_ai_connection_profiles_connection_prof~",
                        column: x => x.connection_profile_id,
                        principalTable: "ai_connection_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_verification_snapshots",
                columns: table => new
                {
                    connection_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    failure_category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    action_hint = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    warnings = table.Column<string>(type: "jsonb", nullable: false),
                    driver_metadata = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_verification_snapshots", x => x.connection_profile_id);
                    table.ForeignKey(
                        name: "FK_ai_verification_snapshots_ai_connection_profiles_connection~",
                        column: x => x.connection_profile_id,
                        principalTable: "ai_connection_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_purpose_bindings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configured_model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    protocol_mode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_purpose_bindings", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_purpose_bindings_ai_configured_models_configured_model_id",
                        column: x => x.configured_model_id,
                        principalTable: "ai_configured_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ai_purpose_bindings_ai_connection_profiles_connection_profi~",
                        column: x => x.connection_profile_id,
                        principalTable: "ai_connection_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_configured_models_connection_model",
                table: "ai_configured_models",
                columns: new[] { "connection_profile_id", "remote_model_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_connection_profiles_client_id_active",
                table: "ai_connection_profiles",
                column: "client_id",
                unique: true,
                filter: "is_active = true");

            migrationBuilder.CreateIndex(
                name: "ix_ai_connection_profiles_client_id_display_name",
                table: "ai_connection_profiles",
                columns: new[] { "client_id", "display_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_purpose_bindings_configured_model_id",
                table: "ai_purpose_bindings",
                column: "configured_model_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_purpose_bindings_connection_purpose",
                table: "ai_purpose_bindings",
                columns: new[] { "connection_profile_id", "purpose" },
                unique: true);

            migrationBuilder.Sql(
                """
INSERT INTO ai_connection_profiles (
    id,
    client_id,
    display_name,
    provider_kind,
    base_url,
    auth_mode,
    protected_secret,
    default_headers,
    default_query_params,
    discovery_mode,
    is_active,
    created_at,
    updated_at)
SELECT
    legacy.id,
    legacy.client_id,
    legacy.display_name,
    'AzureOpenAi',
    legacy.endpoint_url,
    CASE WHEN legacy.api_key IS NULL OR btrim(legacy.api_key) = '' THEN 'AzureIdentity' ELSE 'ApiKey' END,
    legacy.api_key,
    '{}'::jsonb,
    '{}'::jsonb,
    'ManualOnly',
    legacy.is_active,
    legacy.created_at,
    legacy.created_at
FROM ai_connections AS legacy
WHERE legacy.model_category IS NULL OR legacy.model_category = 5;

WITH categorized_only AS (
    SELECT
        legacy.*, 
        row_number() OVER (
            PARTITION BY legacy.client_id
            ORDER BY CASE WHEN legacy.is_active THEN 0 ELSE 1 END, legacy.created_at, legacy.id) AS rn
    FROM ai_connections AS legacy
    WHERE legacy.model_category IS NOT NULL
      AND legacy.model_category <> 5
      AND NOT EXISTS (
          SELECT 1
          FROM ai_connection_profiles AS profile
          WHERE profile.client_id = legacy.client_id)
)
INSERT INTO ai_connection_profiles (
    id,
    client_id,
    display_name,
    provider_kind,
    base_url,
    auth_mode,
    protected_secret,
    default_headers,
    default_query_params,
    discovery_mode,
    is_active,
    created_at,
    updated_at)
SELECT
    legacy.id,
    legacy.client_id,
    legacy.display_name,
    'AzureOpenAi',
    legacy.endpoint_url,
    CASE WHEN legacy.api_key IS NULL OR btrim(legacy.api_key) = '' THEN 'AzureIdentity' ELSE 'ApiKey' END,
    legacy.api_key,
    '{}'::jsonb,
    '{}'::jsonb,
    'ManualOnly',
    true,
    legacy.created_at,
    legacy.created_at
FROM categorized_only AS legacy
WHERE legacy.rn = 1;

WITH connection_targets AS (
    SELECT
        legacy.id AS legacy_connection_id,
        legacy.client_id,
        legacy.model_category,
        legacy.models,
        legacy.active_model,
        legacy.created_at,
        CASE
            WHEN legacy.model_category IS NULL OR legacy.model_category = 5 THEN legacy.id
            ELSE (
                SELECT profile.id
                FROM ai_connection_profiles AS profile
                WHERE profile.client_id = legacy.client_id
                ORDER BY CASE WHEN profile.is_active THEN 0 ELSE 1 END, profile.created_at, profile.id
                LIMIT 1)
        END AS target_profile_id
    FROM ai_connections AS legacy)
INSERT INTO ai_configured_models (
    id,
    connection_profile_id,
    remote_model_id,
    display_name,
    operation_kinds,
    supported_protocol_modes,
    tokenizer_name,
    max_input_tokens,
    embedding_dimensions,
    supports_structured_output,
    supports_tool_use,
    source,
    last_seen_at,
    input_cost_per_1m_usd,
    output_cost_per_1m_usd)
SELECT DISTINCT
    (
        substr(md5(ct.target_profile_id::text || ':' || model.model_name), 1, 8) || '-' ||
        substr(md5(ct.target_profile_id::text || ':' || model.model_name), 9, 4) || '-' ||
        substr(md5(ct.target_profile_id::text || ':' || model.model_name), 13, 4) || '-' ||
        substr(md5(ct.target_profile_id::text || ':' || model.model_name), 17, 4) || '-' ||
        substr(md5(ct.target_profile_id::text || ':' || model.model_name), 21, 12)
    )::uuid,
    ct.target_profile_id,
    model.model_name,
    model.model_name,
    CASE
        WHEN cap.model_name IS NOT NULL OR model.model_name ILIKE '%embedding%'
            THEN '["Embedding"]'::jsonb
        ELSE '["Chat"]'::jsonb
    END,
    CASE
        WHEN cap.model_name IS NOT NULL OR model.model_name ILIKE '%embedding%'
            THEN '["Auto","Embeddings"]'::jsonb
        ELSE '["Auto","Responses","ChatCompletions"]'::jsonb
    END,
    cap.tokenizer_name,
    cap.max_input_tokens,
    cap.embedding_dimensions,
    CASE WHEN cap.model_name IS NOT NULL OR model.model_name ILIKE '%embedding%' THEN false ELSE true END,
    CASE WHEN cap.model_name IS NOT NULL OR model.model_name ILIKE '%embedding%' THEN false ELSE true END,
    'Manual',
    ct.created_at,
    cap.input_cost_per_1m_usd,
    cap.output_cost_per_1m_usd
FROM connection_targets AS ct
CROSS JOIN LATERAL jsonb_array_elements_text(ct.models) AS model(model_name)
LEFT JOIN ai_connection_model_capabilities AS cap
    ON cap.ai_connection_id = ct.legacy_connection_id
   AND lower(cap.model_name) = lower(model.model_name)
ON CONFLICT (connection_profile_id, remote_model_id) DO NOTHING;

WITH connection_targets AS (
    SELECT
        legacy.id AS legacy_connection_id,
        legacy.client_id,
        legacy.model_category,
        legacy.models,
        legacy.active_model,
        legacy.created_at,
        CASE
            WHEN legacy.model_category IS NULL OR legacy.model_category = 5 THEN legacy.id
            ELSE (
                SELECT profile.id
                FROM ai_connection_profiles AS profile
                WHERE profile.client_id = legacy.client_id
                ORDER BY CASE WHEN profile.is_active THEN 0 ELSE 1 END, profile.created_at, profile.id
                LIMIT 1)
        END AS target_profile_id
    FROM ai_connections AS legacy),
binding_defs AS (
    SELECT
        ct.target_profile_id,
        CASE ct.model_category
            WHEN 0 THEN 'ReviewLowEffort'
            WHEN 1 THEN 'ReviewMediumEffort'
            WHEN 2 THEN 'ReviewHighEffort'
            WHEN 3 THEN 'EmbeddingDefault'
            WHEN 4 THEN 'MemoryReconsideration'
            ELSE 'ReviewDefault'
        END AS purpose,
        COALESCE(ct.active_model, model.model_name) AS model_name,
        CASE WHEN ct.model_category = 3 THEN 'Embeddings' ELSE 'Auto' END AS protocol_mode,
        ct.created_at
    FROM connection_targets AS ct
    CROSS JOIN LATERAL (
        SELECT jsonb_array_elements_text(ct.models) AS model_name
        LIMIT 1) AS model)
INSERT INTO ai_purpose_bindings (
    id,
    connection_profile_id,
    configured_model_id,
    purpose,
    protocol_mode,
    is_enabled,
    created_at,
    updated_at)
SELECT
    (
        substr(md5(binding.target_profile_id::text || ':binding:' || binding.purpose), 1, 8) || '-' ||
        substr(md5(binding.target_profile_id::text || ':binding:' || binding.purpose), 9, 4) || '-' ||
        substr(md5(binding.target_profile_id::text || ':binding:' || binding.purpose), 13, 4) || '-' ||
        substr(md5(binding.target_profile_id::text || ':binding:' || binding.purpose), 17, 4) || '-' ||
        substr(md5(binding.target_profile_id::text || ':binding:' || binding.purpose), 21, 12)
    )::uuid,
    binding.target_profile_id,
    model.id,
    binding.purpose,
    binding.protocol_mode,
    true,
    binding.created_at,
    binding.created_at
FROM binding_defs AS binding
JOIN ai_configured_models AS model
    ON model.connection_profile_id = binding.target_profile_id
   AND lower(model.remote_model_id) = lower(binding.model_name)
ON CONFLICT (connection_profile_id, purpose) DO NOTHING;

INSERT INTO ai_purpose_bindings (
    id,
    connection_profile_id,
    configured_model_id,
    purpose,
    protocol_mode,
    is_enabled,
    created_at,
    updated_at)
SELECT
    (
        substr(md5(profile.id::text || ':binding:ReviewDefault'), 1, 8) || '-' ||
        substr(md5(profile.id::text || ':binding:ReviewDefault'), 9, 4) || '-' ||
        substr(md5(profile.id::text || ':binding:ReviewDefault'), 13, 4) || '-' ||
        substr(md5(profile.id::text || ':binding:ReviewDefault'), 17, 4) || '-' ||
        substr(md5(profile.id::text || ':binding:ReviewDefault'), 21, 12)
    )::uuid,
    profile.id,
    model.id,
    'ReviewDefault',
    'Auto',
    true,
    profile.created_at,
    profile.created_at
FROM ai_connection_profiles AS profile
JOIN LATERAL (
    SELECT configured.id
    FROM ai_configured_models AS configured
    WHERE configured.connection_profile_id = profile.id
    ORDER BY configured.last_seen_at NULLS LAST, configured.remote_model_id
    LIMIT 1) AS model ON true
WHERE NOT EXISTS (
    SELECT 1
    FROM ai_purpose_bindings AS binding
    WHERE binding.connection_profile_id = profile.id
      AND binding.purpose = 'ReviewDefault');

INSERT INTO ai_verification_snapshots (
    connection_profile_id,
    status,
    failure_category,
    summary,
    action_hint,
    checked_at,
    warnings,
    driver_metadata)
SELECT
    profile.id,
    'NeverVerified',
    NULL,
    'Legacy AI connection migrated to the provider-neutral profile format. Re-run verification to refresh provider diagnostics.',
    'Use the Verify action on the profile to refresh provider reachability and binding validation.',
    NULL,
    '[]'::jsonb,
    NULL
FROM ai_connection_profiles AS profile
ON CONFLICT (connection_profile_id) DO NOTHING;
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_purpose_bindings");

            migrationBuilder.DropTable(
                name: "ai_verification_snapshots");

            migrationBuilder.DropTable(
                name: "ai_configured_models");

            migrationBuilder.DropTable(
                name: "ai_connection_profiles");
        }
    }
}
