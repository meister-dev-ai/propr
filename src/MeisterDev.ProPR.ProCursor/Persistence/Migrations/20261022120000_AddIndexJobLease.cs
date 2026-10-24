// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MeisterDev.ProPR.ProCursor.Persistence.Migrations;

/// <summary>
///     Adds the lease of the ProCursor service that runs an index job. A job in processing that was started before
///     this migration has no lease, and the index worker treats it as a job whose lease has expired.
/// </summary>
[DbContext(typeof(ProCursorOperationalDbContext))]
[Migration("20261022120000_AddIndexJobLease")]
public sealed class AddIndexJobLease : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "ALTER TABLE procursor_index_jobs ADD COLUMN IF NOT EXISTS lease_expires_at timestamp with time zone;");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE procursor_index_jobs DROP COLUMN IF EXISTS lease_expires_at;");
    }
}
