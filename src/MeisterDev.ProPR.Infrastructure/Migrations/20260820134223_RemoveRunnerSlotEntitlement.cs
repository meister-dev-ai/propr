using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Drops the stored runner slot count.
    /// </summary>
    /// <remarks>
    ///     How many runners an installation may hold comes from the license it has activated, and it is enforced
    ///     when a runner enrolls. An earlier build metered lease admission against this column instead, which an
    ///     operator with database access could change. Nothing reads it any more, so the values it holds only
    ///     describe a limit that is no longer applied.
    /// </remarks>
    public partial class RemoveRunnerSlotEntitlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "entitled_runner_slots",
                table: "installation_edition");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The column comes back empty. The counts it held are gone with it, and no build reads them.
            migrationBuilder.AddColumn<int>(
                name: "entitled_runner_slots",
                table: "installation_edition",
                type: "integer",
                nullable: true);
        }
    }
}
