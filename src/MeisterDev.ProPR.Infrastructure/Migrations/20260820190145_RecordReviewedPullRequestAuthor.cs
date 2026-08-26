using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Records the account that opened the reviewed pull request on the review job.
    /// </summary>
    /// <remarks>
    ///     The provider, the host and the identifier together name the account, and the job already carries the
    ///     first two, so only the identifier, the two labels and the bot signal are added. Every column is
    ///     nullable and nothing is backfilled: the author is known only from a fetch, and a job that has already
    ///     run will not be fetched again.
    /// </remarks>
    public partial class RecordReviewedPullRequestAuthor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pr_author_display_name",
                table: "review_jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pr_author_external_user_id",
                table: "review_jobs",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "pr_author_is_bot",
                table: "review_jobs",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pr_author_login",
                table: "review_jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pr_author_display_name",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "pr_author_external_user_id",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "pr_author_is_bot",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "pr_author_login",
                table: "review_jobs");
        }
    }
}
