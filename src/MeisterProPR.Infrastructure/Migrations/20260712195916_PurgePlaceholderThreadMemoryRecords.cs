// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PurgePlaceholderThreadMemoryRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time cleanup of worthless memory that entered the store before the store-time
            // clarity gate existed: resolved-thread records whose summary is the exact failure
            // placeholder carry no determinable resolution. Scoped to memory_source = 0
            // (ThreadResolved) so admin-dismissed records are never touched. Idempotent.
            migrationBuilder.Sql(
                "DELETE FROM thread_memory_records " +
                "WHERE resolution_summary = 'Thread was resolved. No AI-generated summary could be produced at this time.' " +
                "AND memory_source = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible data cleanup; no down migration.
        }
    }
}
