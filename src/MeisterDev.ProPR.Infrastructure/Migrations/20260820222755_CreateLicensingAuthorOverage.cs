using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Adds the record of the calendar months whose counted authors went above the number the license
    ///     states for them.
    /// </summary>
    /// <remarks>
    ///     Nothing is backfilled. The comparison needs both the month's authors and the license that was in
    ///     force while the month ran, and only the first of those is on record for a month that has passed, so
    ///     the record begins with the first evaluation after the upgrade. The rows are descriptive: nothing
    ///     about what the installation may run is decided from them.
    /// </remarks>
    public partial class CreateLicensingAuthorOverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "licensing_author_overage",
                columns: table => new
                {
                    overage_month = table.Column<DateOnly>(type: "date", nullable: false),
                    licensed_count = table.Column<long>(type: "bigint", nullable: false),
                    highest_observed_count = table.Column<long>(type: "bigint", nullable: false),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_licensing_author_overage", x => x.overage_month);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "licensing_author_overage");
        }
    }
}
