// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProCursorTokenUsageReporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "input_cost_per_1m_usd",
                table: "ai_connection_model_capabilities",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "output_cost_per_1m_usd",
                table: "ai_connection_model_capabilities",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "procursor_token_usage_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    procursor_source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_display_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    index_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    call_type = table.Column<short>(type: "smallint", nullable: false),
                    ai_connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deployment_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tokenizer_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    prompt_tokens = table.Column<long>(type: "bigint", nullable: false),
                    completion_tokens = table.Column<long>(type: "bigint", nullable: false),
                    total_tokens = table.Column<long>(type: "bigint", nullable: false),
                    tokens_estimated = table.Column<bool>(type: "boolean", nullable: false),
                    estimated_cost_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    cost_estimated = table.Column<bool>(type: "boolean", nullable: false),
                    resource_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_path = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    knowledge_chunk_id = table.Column<Guid>(type: "uuid", nullable: true),
                    safe_metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procursor_token_usage_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "procursor_token_usage_rollups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    procursor_source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_display_name_snapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    bucket_start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    granularity = table.Column<short>(type: "smallint", nullable: false),
                    model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    prompt_tokens = table.Column<long>(type: "bigint", nullable: false),
                    completion_tokens = table.Column<long>(type: "bigint", nullable: false),
                    total_tokens = table.Column<long>(type: "bigint", nullable: false),
                    estimated_cost_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    event_count = table.Column<long>(type: "bigint", nullable: false),
                    estimated_event_count = table.Column<long>(type: "bigint", nullable: false),
                    last_recomputed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_procursor_token_usage_rollups", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_procursor_token_usage_events_client_model_occurred_at",
                table: "procursor_token_usage_events",
                columns: new[] { "client_id", "model_name", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_procursor_token_usage_events_client_occurred_at",
                table: "procursor_token_usage_events",
                columns: new[] { "client_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_procursor_token_usage_events_index_job_id",
                table: "procursor_token_usage_events",
                column: "index_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_procursor_token_usage_events_source_occurred_at",
                table: "procursor_token_usage_events",
                columns: new[] { "procursor_source_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_procursor_token_usage_events_client_request",
                table: "procursor_token_usage_events",
                columns: new[] { "client_id", "request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_procursor_token_usage_rollups_client_granularity_bucket",
                table: "procursor_token_usage_rollups",
                columns: new[] { "client_id", "granularity", "bucket_start_date" });

            migrationBuilder.CreateIndex(
                name: "ix_procursor_token_usage_rollups_client_model_granularity_bucket",
                table: "procursor_token_usage_rollups",
                columns: new[] { "client_id", "model_name", "granularity", "bucket_start_date" });

            migrationBuilder.CreateIndex(
                name: "ix_procursor_token_usage_rollups_source_granularity_bucket",
                table: "procursor_token_usage_rollups",
                columns: new[] { "client_id", "procursor_source_id", "granularity", "bucket_start_date" });

            migrationBuilder.CreateIndex(
                name: "ux_procursor_token_usage_rollups_scope",
                table: "procursor_token_usage_rollups",
                columns: new[] { "client_id", "procursor_source_id", "bucket_start_date", "granularity", "model_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "procursor_token_usage_events");

            migrationBuilder.DropTable(
                name: "procursor_token_usage_rollups");

            migrationBuilder.DropColumn(
                name: "input_cost_per_1m_usd",
                table: "ai_connection_model_capabilities");

            migrationBuilder.DropColumn(
                name: "output_cost_per_1m_usd",
                table: "ai_connection_model_capabilities");
        }
    }
}
