// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenBreakdownAndProtocolTier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "memory_source",
                table: "thread_memory_records",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string>(
                name: "token_breakdown",
                table: "review_jobs",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<short>(
                name: "ai_connection_category",
                table: "review_job_protocols",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_id",
                table: "review_job_protocols",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "memory_source",
                table: "thread_memory_records");

            migrationBuilder.DropColumn(
                name: "token_breakdown",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "ai_connection_category",
                table: "review_job_protocols");

            migrationBuilder.DropColumn(
                name: "model_id",
                table: "review_job_protocols");
        }
    }
}
