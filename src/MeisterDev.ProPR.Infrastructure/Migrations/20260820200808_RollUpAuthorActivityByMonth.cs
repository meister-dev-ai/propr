using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Adds the month-and-author rollup, and the column that lets an answered mention name its author the
    ///     same way a reviewed pull request does.
    /// </summary>
    /// <remarks>
    ///     Nothing is backfilled. The host's own identifier for a comment author is only known from the payload
    ///     the scan read, and a job that has already run will not be scanned again, so the count begins with the
    ///     work that completes after the upgrade. The exclusion flag is created here and written false; the rule
    ///     that decides which authors are left out arrives separately and only sets flags.
    /// </remarks>
    public partial class RollUpAuthorActivityByMonth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "comment_author_native_id",
                table: "mention_reply_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "licensing_author_activity",
                columns: table => new
                {
                    activity_month = table.Column<DateOnly>(type: "date", nullable: false),
                    author_key = table.Column<string>(type: "character varying(768)", maxLength: 768, nullable: false),
                    excluded = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    provider = table.Column<int>(type: "integer", nullable: false),
                    host_base_url = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    external_user_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    first_seen_source = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_licensing_author_activity", x => new { x.activity_month, x.author_key });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "licensing_author_activity");

            migrationBuilder.DropColumn(
                name: "comment_author_native_id",
                table: "mention_reply_jobs");
        }
    }
}
