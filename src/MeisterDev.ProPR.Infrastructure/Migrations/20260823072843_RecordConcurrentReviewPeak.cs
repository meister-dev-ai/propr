using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Adds the record of the highest number of reviews seen executing at the same time on a UTC day.
    /// </summary>
    /// <remarks>
    ///     Nothing is backfilled. The number is observed as reviews are claimed, and a day that has passed
    ///     left no reading to recover it from, so the record begins with the first claim after the upgrade.
    ///     The rows are descriptive: nothing about what the installation may run is decided from them.
    /// </remarks>
    public partial class RecordConcurrentReviewPeak : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "licensing_concurrent_review_peak",
                columns: table => new
                {
                    peak_date = table.Column<DateOnly>(type: "date", nullable: false),
                    peak_count = table.Column<long>(type: "bigint", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_licensing_concurrent_review_peak", x => x.peak_date);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "licensing_concurrent_review_peak");
        }
    }
}
