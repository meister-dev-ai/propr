using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WidenPostedCommentOriginKeyWithThread : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_posted_comment_origins_comment",
                table: "posted_comment_origins");

            migrationBuilder.CreateIndex(
                name: "uq_posted_comment_origins_comment",
                table: "posted_comment_origins",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "provider_thread_id", "provider_comment_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "uq_posted_comment_origins_comment",
                table: "posted_comment_origins");

            migrationBuilder.CreateIndex(
                name: "uq_posted_comment_origins_comment",
                table: "posted_comment_origins",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "provider_comment_id" },
                unique: true);
        }
    }
}
