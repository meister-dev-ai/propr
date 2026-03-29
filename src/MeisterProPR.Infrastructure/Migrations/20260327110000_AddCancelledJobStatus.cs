using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    ///     No DDL required: <c>JobStatus.Cancelled = 4</c> is stored as an integer in the
    ///     <c>review_jobs.status</c> TEXT column via EF Core value-conversion.
    ///     This empty migration serves as a recorded checkpoint in the migration history.
    /// </remarks>
    public partial class AddCancelledJobStatus : Migration
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
