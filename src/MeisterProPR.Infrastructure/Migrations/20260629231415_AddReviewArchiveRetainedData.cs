using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewArchiveRetainedData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "retained_pull_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    pull_request_id = table.Column<long>(type: "bigint", nullable: false),
                    pr_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retained_pull_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "retained_file_diffs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    retained_pull_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    file_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    change_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_binary = table.Column<bool>(type: "boolean", nullable: false),
                    encrypted_unified_diff = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retained_file_diffs", x => x.id);
                    table.ForeignKey(
                        name: "FK_retained_file_diffs_retained_pull_requests_retained_pull_re~",
                        column: x => x.retained_pull_request_id,
                        principalTable: "retained_pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "retained_threads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    retained_pull_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    file_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    line = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retained_threads", x => x.id);
                    table.ForeignKey(
                        name: "FK_retained_threads_retained_pull_requests_retained_pull_reque~",
                        column: x => x.retained_pull_request_id,
                        principalTable: "retained_pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "retained_thread_comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    retained_thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    author_identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    is_ai_authored = table.Column<bool>(type: "boolean", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    encrypted_text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retained_thread_comments", x => x.id);
                    table.ForeignKey(
                        name: "FK_retained_thread_comments_retained_threads_retained_thread_id",
                        column: x => x.retained_thread_id,
                        principalTable: "retained_threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_retained_file_diffs_identity",
                table: "retained_file_diffs",
                columns: new[] { "retained_pull_request_id", "revision_key", "file_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_retained_pull_requests_connection_id",
                table: "retained_pull_requests",
                column: "connection_id");

            migrationBuilder.CreateIndex(
                name: "ix_retained_pull_requests_last_activity_at",
                table: "retained_pull_requests",
                column: "last_activity_at");

            migrationBuilder.CreateIndex(
                name: "uq_retained_pull_requests_identity",
                table: "retained_pull_requests",
                columns: new[] { "client_id", "connection_id", "repository_id", "pull_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_retained_thread_comments_thread_id",
                table: "retained_thread_comments",
                column: "retained_thread_id");

            migrationBuilder.CreateIndex(
                name: "uq_retained_threads_identity",
                table: "retained_threads",
                columns: new[] { "retained_pull_request_id", "thread_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "retained_file_diffs");

            migrationBuilder.DropTable(
                name: "retained_thread_comments");

            migrationBuilder.DropTable(
                name: "retained_threads");

            migrationBuilder.DropTable(
                name: "retained_pull_requests");
        }
    }
}
