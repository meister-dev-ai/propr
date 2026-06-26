// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CloseOrphanedProtocols : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time data cleanup: close protocol rows left open (completed_at IS NULL) by jobs that
            // already reached a terminal state. These are abandoned in-flight passes that the new
            // JobRepository reconcile invariant closes going forward; this back-fills the pre-existing ones.
            // Idempotent (the WHERE clause matches nothing on a second run); no schema change.
            migrationBuilder.Sql(
                "UPDATE review_job_protocols SET completed_at = now(), outcome = 'Abandoned' WHERE completed_at IS NULL AND job_id IN (SELECT id FROM review_jobs WHERE status IN ('Completed','Failed','Cancelled'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible data cleanup; no down migration.
        }
    }
}
