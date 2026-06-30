using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPostedCommentOrigins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "posted_comment_origins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    pull_request_id = table.Column<long>(type: "bigint", nullable: false),
                    provider_thread_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    provider_comment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_posted_comment_origins", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_posted_comment_origins_pull_request",
                table: "posted_comment_origins",
                columns: new[] { "client_id", "repository_id", "pull_request_id" });

            migrationBuilder.CreateIndex(
                name: "uq_posted_comment_origins_comment",
                table: "posted_comment_origins",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "provider_comment_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "posted_comment_origins");
        }
    }
}
