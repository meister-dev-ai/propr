using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenOptimizationProtocolDiagnostics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "cached_input_tokens",
                table: "protocol_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "cache_status",
                table: "protocol_events",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string>(
                name: "cache_miss_category",
                table: "protocol_events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "prefix_eligibility",
                table: "protocol_events",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string>(
                name: "tool_evidence_action",
                table: "protocol_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tool_evidence_source_tool_name",
                table: "protocol_events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tool_evidence_original_payload_tokens",
                table: "protocol_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tool_evidence_bounded_payload_tokens",
                table: "protocol_events",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "tool_evidence_refreshable",
                table: "protocol_events",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "finalization_attempt_kind",
                table: "protocol_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "finalization_reason",
                table: "protocol_events",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "finalization_outcome",
                table: "protocol_events",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "total_cached_input_tokens",
                table: "review_job_protocols",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "cache_observability",
                table: "review_job_protocols",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "cached_input_tokens", table: "protocol_events");
            migrationBuilder.DropColumn(name: "cache_status", table: "protocol_events");
            migrationBuilder.DropColumn(name: "cache_miss_category", table: "protocol_events");
            migrationBuilder.DropColumn(name: "prefix_eligibility", table: "protocol_events");
            migrationBuilder.DropColumn(name: "tool_evidence_action", table: "protocol_events");
            migrationBuilder.DropColumn(name: "tool_evidence_source_tool_name", table: "protocol_events");
            migrationBuilder.DropColumn(name: "tool_evidence_original_payload_tokens", table: "protocol_events");
            migrationBuilder.DropColumn(name: "tool_evidence_bounded_payload_tokens", table: "protocol_events");
            migrationBuilder.DropColumn(name: "tool_evidence_refreshable", table: "protocol_events");
            migrationBuilder.DropColumn(name: "finalization_attempt_kind", table: "protocol_events");
            migrationBuilder.DropColumn(name: "finalization_reason", table: "protocol_events");
            migrationBuilder.DropColumn(name: "finalization_outcome", table: "protocol_events");
            migrationBuilder.DropColumn(name: "total_cached_input_tokens", table: "review_job_protocols");
            migrationBuilder.DropColumn(name: "cache_observability", table: "review_job_protocols");
        }
    }
}
