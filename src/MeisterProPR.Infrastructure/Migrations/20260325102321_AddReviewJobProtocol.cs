// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewJobProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "confidence_evaluations",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "final_confidence",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "tool_call_count",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "tool_calls",
                table: "review_jobs");

            migrationBuilder.CreateTable(
                name: "review_job_protocols",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    total_input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    total_output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    iteration_count = table.Column<int>(type: "integer", nullable: true),
                    tool_call_count = table.Column<int>(type: "integer", nullable: true),
                    final_confidence = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_job_protocols", x => x.id);
                    table.ForeignKey(
                        name: "FK_review_job_protocols_review_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "review_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "protocol_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    protocol_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    input_text_sample = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    output_summary = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_protocol_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_protocol_events_review_job_protocols_protocol_id",
                        column: x => x.protocol_id,
                        principalTable: "review_job_protocols",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_protocol_events_protocol_id",
                table: "protocol_events",
                column: "protocol_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_job_protocols_job_id",
                table: "review_job_protocols",
                column: "job_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "protocol_events");

            migrationBuilder.DropTable(
                name: "review_job_protocols");

            migrationBuilder.AddColumn<string>(
                name: "confidence_evaluations",
                table: "review_jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "final_confidence",
                table: "review_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tool_call_count",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "tool_calls",
                table: "review_jobs",
                type: "jsonb",
                nullable: true);
        }
    }
}
