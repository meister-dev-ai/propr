// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    ///     No DDL required: the new <c>JobStatus.Superseded</c> value is persisted as its name in the
    ///     <c>review_jobs.status</c> TEXT column via EF Core value-conversion, so no schema change is needed.
    ///     This empty migration serves as a recorded checkpoint in the migration history.
    /// </remarks>
    public partial class AddSupersededJobStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No schema changes required.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No schema changes to revert.
        }
    }
}
