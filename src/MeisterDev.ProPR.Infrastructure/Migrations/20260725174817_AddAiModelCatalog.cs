using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiModelCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "cache_write_cost_per_1m_usd",
                table: "ai_configured_models",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reasoning_content_field",
                table: "ai_configured_models",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "supports_prompt_caching",
                table: "ai_configured_models",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "supports_reasoning",
                table: "ai_configured_models",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ai_model_catalog_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    provider_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    remote_model_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    family = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    supports_tool_use = table.Column<bool>(type: "boolean", nullable: false),
                    supports_structured_output = table.Column<bool>(type: "boolean", nullable: false),
                    supports_reasoning = table.Column<bool>(type: "boolean", nullable: false),
                    supports_prompt_caching = table.Column<bool>(type: "boolean", nullable: false),
                    reasoning_content_field = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    max_context_tokens = table.Column<int>(type: "integer", nullable: true),
                    max_output_tokens = table.Column<int>(type: "integer", nullable: true),
                    input_cost_per_1m_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    output_cost_per_1m_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    cached_input_cost_per_1m_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    cache_write_cost_per_1m_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    open_weights = table.Column<bool>(type: "boolean", nullable: false),
                    release_date = table.Column<DateOnly>(type: "date", nullable: true),
                    source_format = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_model_catalog_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_model_catalog_entries_provider",
                table: "ai_model_catalog_entries",
                column: "provider_id");

            migrationBuilder.CreateIndex(
                name: "ux_ai_model_catalog_entries_client",
                table: "ai_model_catalog_entries",
                columns: new[] { "client_id", "provider_id", "remote_model_id" },
                unique: true,
                filter: "client_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_ai_model_catalog_entries_global",
                table: "ai_model_catalog_entries",
                columns: new[] { "provider_id", "remote_model_id" },
                unique: true,
                filter: "tenant_id IS NULL AND client_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_ai_model_catalog_entries_tenant",
                table: "ai_model_catalog_entries",
                columns: new[] { "tenant_id", "provider_id", "remote_model_id" },
                unique: true,
                filter: "tenant_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_model_catalog_entries");

            migrationBuilder.DropColumn(
                name: "cache_write_cost_per_1m_usd",
                table: "ai_configured_models");

            migrationBuilder.DropColumn(
                name: "reasoning_content_field",
                table: "ai_configured_models");

            migrationBuilder.DropColumn(
                name: "supports_prompt_caching",
                table: "ai_configured_models");

            migrationBuilder.DropColumn(
                name: "supports_reasoning",
                table: "ai_configured_models");
        }
    }
}
