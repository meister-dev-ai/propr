using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Removes the language-robust screening and evidence-backed verification switches from the client model and
    ///     leaves their columns in the database.
    /// </summary>
    /// <remarks>
    ///     The columns <c>clients.enable_evidence_backed_verification</c> and
    ///     <c>clients.enable_language_robust_screening</c> stay for one release. A control plane of the previous version
    ///     that still runs during a rolling deploy reads and writes them, and a rollback to that version needs them. Both
    ///     columns are not null with a default of false, so rows that this version inserts get a value. A later release
    ///     drops the columns.
    /// </remarks>
    public partial class RetireClientScreeningAndVerificationSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The model no longer maps the two columns. They are kept in the database for this release.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Up changes nothing in the database, so there is nothing to undo.
        }
    }
}
