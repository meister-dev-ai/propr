// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RestoreReviewStrategyFoundations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "comparison_group_id",
                table: "review_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "review_comparison_mode",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "review_publication_mode",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "review_strategy",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "review_strategy_selection_source",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "default_review_strategy",
                table: "clients",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "review_mode_run_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comparison_group_id = table.Column<Guid>(type: "uuid", nullable: true),
                    strategy = table.Column<int>(type: "integer", nullable: false),
                    publication_mode = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    result_json = table.Column<string>(type: "jsonb", nullable: true),
                    stage_metrics_json = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_mode_run_results", x => x.id);
                    table.ForeignKey(
                        name: "FK_review_mode_run_results_review_jobs_review_job_id",
                        column: x => x.review_job_id,
                        principalTable: "review_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_review_jobs_comparison_group_id",
                table: "review_jobs",
                column: "comparison_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_mode_run_results_comparison_group_id",
                table: "review_mode_run_results",
                column: "comparison_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_mode_run_results_review_job_id",
                table: "review_mode_run_results",
                column: "review_job_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_mode_run_results");

            migrationBuilder.DropIndex(
                name: "ix_review_jobs_comparison_group_id",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "comparison_group_id",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "review_comparison_mode",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "review_publication_mode",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "review_strategy",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "review_strategy_selection_source",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "default_review_strategy",
                table: "clients");
        }
    }
}
