using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Records how many harvested threads had not settled when a measurement was taken, so recall can be
    ///     reported only where both sides of the ratio were complete.
    /// </summary>
    /// <remarks>
    ///     Nullable and deliberately not backfilled. A seal taken before this column existed cannot have its
    ///     coverage reconstructed, and defaulting it to zero would assert that every historical measurement was
    ///     complete: those are precisely the rows whose recall the coverage gate exists to withhold.
    /// </remarks>
    public partial class CoverageGateRecall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "unsettled_miss_count",
                table: "code_insight_pull_request_metrics",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "unsettled_miss_count",
                table: "code_insight_pull_request_metrics");
        }
    }
}
