using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewTraceTimingProtocolEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "active_duration_ms",
                table: "protocol_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                table: "protocol_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "duration_ms",
                table: "protocol_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "phase_timings",
                table: "protocol_events",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "started_at",
                table: "protocol_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "timing_availability",
                table: "protocol_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tool_outcome",
                table: "protocol_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "wait_duration_ms",
                table: "protocol_events",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "active_duration_ms",
                table: "protocol_events");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "protocol_events");

            migrationBuilder.DropColumn(
                name: "duration_ms",
                table: "protocol_events");

            migrationBuilder.DropColumn(
                name: "phase_timings",
                table: "protocol_events");

            migrationBuilder.DropColumn(
                name: "started_at",
                table: "protocol_events");

            migrationBuilder.DropColumn(
                name: "timing_availability",
                table: "protocol_events");

            migrationBuilder.DropColumn(
                name: "tool_outcome",
                table: "protocol_events");

            migrationBuilder.DropColumn(
                name: "wait_duration_ms",
                table: "protocol_events");
        }
    }
}
