// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryActivityLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "memory_activity_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<int>(type: "integer", nullable: false),
                    repository_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    pull_request_id = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<int>(type: "integer", nullable: false),
                    previous_status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    current_status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_memory_activity_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_memory_activity_log_client_thread",
                table: "memory_activity_log",
                columns: new[] { "client_id", "thread_id" });

            migrationBuilder.CreateIndex(
                name: "ix_memory_activity_log_occurred_at",
                table: "memory_activity_log",
                column: "occurred_at",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "memory_activity_log");
        }
    }
}
