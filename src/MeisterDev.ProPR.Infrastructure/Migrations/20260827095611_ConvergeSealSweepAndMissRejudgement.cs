using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Adds the seal sweep's per-aggregate attempt timestamp, and records on each harvested human thread
    ///     whether the thread was resolved when its stored judgement was made.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>last_seal_attempt_at</c> starts null on every existing row, which places them all ahead of
    ///         anything already examined once the sweep begins ordering by it. That is the intended recovery: the
    ///         backlog that accumulated while the sweep re-examined only its newest rows is drained before any row
    ///         is revisited.
    ///     </para>
    ///     <para>
    ///         <c>judged_thread_resolved</c> starts false for every existing row. The state a historical judgement
    ///         was made against was never recorded, so it cannot be reconstructed, and false is both the honest
    ///         answer and the useful one: it marks the judgement provisional, so a thread still being observed is
    ///         judged again once it resolves. A thread whose pull request has already closed is no longer observed
    ///         and keeps the verdict it has.
    ///     </para>
    ///     <para>
    ///         <c>last_judged_at</c> is backfilled from <c>harvested_at</c> rather than left at a sentinel. The two
    ///         columns differing is what shows a row was re-judged, and a sentinel would make every historical row
    ///         read as though it had been.
    ///     </para>
    /// </remarks>
    public partial class ConvergeSealSweepAndMissRejudgement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_seal_attempt_at",
                table: "code_insight_pull_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "judged_thread_resolved",
                table: "code_insight_misses",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Added with a transient default so the column can be NOT NULL over existing rows, then backfilled
            // from the harvest time and the default dropped, leaving the column as the model declares it.
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_judged_at",
                table: "code_insight_misses",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.Sql(
                "UPDATE code_insight_misses SET last_judged_at = harvested_at;");

            migrationBuilder.Sql(
                "ALTER TABLE code_insight_misses ALTER COLUMN last_judged_at DROP DEFAULT;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_seal_attempt_at",
                table: "code_insight_pull_requests");

            migrationBuilder.DropColumn(
                name: "judged_thread_resolved",
                table: "code_insight_misses");

            migrationBuilder.DropColumn(
                name: "last_judged_at",
                table: "code_insight_misses");
        }
    }
}
