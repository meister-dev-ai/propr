using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Records how far back a mention scan is still sure of, apart from when it last ran.
    /// </summary>
    /// <remarks>
    ///     One column served both, so a tick that a throttle or an unreadable repository left partial moved the
    ///     discovery window forward over ground it had not read, and a question asked in that window was never
    ///     asked for again. The two cannot share a column: the configuration has still been scanned, so its
    ///     interval must advance or every tick would scan it again. Null on every existing row, which falls
    ///     back to the old value and so carries an installation across with the window it already had.
    /// </remarks>
    public partial class HoldMentionScanWindowOpenOnPartialTick : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_complete_scan_at",
                table: "mention_project_scans",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_complete_scan_at",
                table: "mention_project_scans");
        }
    }
}
