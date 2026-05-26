using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResumedFileResultSourceTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "resumed_from_file_result_id",
                table: "review_file_results",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "resumed_from_job_id",
                table: "review_file_results",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_file_results_resumed_from_file_result_id",
                table: "review_file_results",
                column: "resumed_from_file_result_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_file_results_resumed_from_job_id",
                table: "review_file_results",
                column: "resumed_from_job_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_review_file_results_resumed_from_file_result_id",
                table: "review_file_results");

            migrationBuilder.DropIndex(
                name: "ix_review_file_results_resumed_from_job_id",
                table: "review_file_results");

            migrationBuilder.DropColumn(
                name: "resumed_from_file_result_id",
                table: "review_file_results");

            migrationBuilder.DropColumn(
                name: "resumed_from_job_id",
                table: "review_file_results");
        }
    }
}
