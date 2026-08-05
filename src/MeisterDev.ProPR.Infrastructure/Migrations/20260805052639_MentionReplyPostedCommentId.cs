using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MentionReplyPostedCommentId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "posted_reply_comment_id",
                table: "mention_reply_jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "posted_reply_comment_id",
                table: "mention_reply_jobs");
        }
    }
}
