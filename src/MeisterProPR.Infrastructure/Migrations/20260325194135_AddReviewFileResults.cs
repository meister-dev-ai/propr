using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewFileResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_review_job_protocols_job_id",
                table: "review_job_protocols");

            migrationBuilder.AddColumn<int>(
                name: "retry_count",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "file_result_id",
                table: "review_job_protocols",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "label",
                table: "review_job_protocols",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "review_file_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_path = table.Column<string>(type: "text", nullable: false),
                    is_complete = table.Column<bool>(type: "boolean", nullable: false),
                    is_failed = table.Column<bool>(type: "boolean", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    per_file_summary = table.Column<string>(type: "text", nullable: true),
                    comments_json = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_file_results", x => x.id);
                    table.ForeignKey(
                        name: "FK_review_file_results_review_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "review_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_review_job_protocols_file_result_id",
                table: "review_job_protocols",
                column: "file_result_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_job_protocols_job_id",
                table: "review_job_protocols",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_file_results_job_file",
                table: "review_file_results",
                columns: new[] { "job_id", "file_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_file_results_job_id",
                table: "review_file_results",
                column: "job_id");

            migrationBuilder.AddForeignKey(
                name: "FK_review_job_protocols_review_file_results_file_result_id",
                table: "review_job_protocols",
                column: "file_result_id",
                principalTable: "review_file_results",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_review_job_protocols_review_file_results_file_result_id",
                table: "review_job_protocols");

            migrationBuilder.DropTable(
                name: "review_file_results");

            migrationBuilder.DropIndex(
                name: "ix_review_job_protocols_file_result_id",
                table: "review_job_protocols");

            migrationBuilder.DropIndex(
                name: "ix_review_job_protocols_job_id",
                table: "review_job_protocols");

            migrationBuilder.DropColumn(
                name: "retry_count",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "file_result_id",
                table: "review_job_protocols");

            migrationBuilder.DropColumn(
                name: "label",
                table: "review_job_protocols");

            migrationBuilder.CreateIndex(
                name: "ix_review_job_protocols_job_id",
                table: "review_job_protocols",
                column: "job_id",
                unique: true);
        }
    }
}
