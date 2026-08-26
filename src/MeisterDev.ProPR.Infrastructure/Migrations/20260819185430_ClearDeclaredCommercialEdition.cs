using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Clears the stored commercial edition declaration and the activation stamp that went with it.
    /// </summary>
    /// <remarks>
    ///     The edition an installation runs as follows from the license it has activated. An earlier build let an
    ///     administrator declare the commercial edition instead, and installations that did still carry that
    ///     declaration and the instant and user it was made by. Nothing reads those columns for entitlement any
    ///     more, so the stored values only describe a state the installation is not in. An installation that never
    ///     declared the commercial edition matches nothing here, and neither does one this has already run over.
    /// </remarks>
    public partial class ClearDeclaredCommercialEdition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1 was the declared commercial edition, 0 is community. The runner slot entitlement on the same row
            // was never part of that declaration and is left as it is, so runner leasing keeps the ceiling the
            // installation is configured with.
            migrationBuilder.Sql(
                """
                UPDATE installation_edition
                SET edition = 0,
                    activated_at = NULL,
                    activated_by_user_id = NULL
                WHERE edition = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The cleared values declared an entitlement no build grants without a license, so there is nothing
            // to restore.
        }
    }
}
