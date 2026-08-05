using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ThreadPassSpendAndTrace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ai_connection_id",
                table: "thread_pass_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_model",
                table: "thread_pass_jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "budget_block_cap_kind",
                table: "thread_pass_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "budget_block_scope",
                table: "thread_pass_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "budget_block_spent_usd",
                table: "thread_pass_jobs",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "budget_block_threshold_usd",
                table: "thread_pass_jobs",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "cost_is_approximate",
                table: "thread_pass_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "total_estimated_cost_usd",
                table: "thread_pass_jobs",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "total_input_tokens",
                table: "thread_pass_jobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "total_output_tokens",
                table: "thread_pass_jobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AlterColumn<Guid>(
                name: "job_id",
                table: "review_job_protocols",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "thread_pass_job_id",
                table: "review_job_protocols",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_job_protocols_thread_pass_job_id",
                table: "review_job_protocols",
                column: "thread_pass_job_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_review_job_protocols_single_owner",
                table: "review_job_protocols",
                sql: "(job_id IS NULL) <> (thread_pass_job_id IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_review_job_protocols_thread_pass_jobs_thread_pass_job_id",
                table: "review_job_protocols",
                column: "thread_pass_job_id",
                principalTable: "thread_pass_jobs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_review_job_protocols_thread_pass_jobs_thread_pass_job_id",
                table: "review_job_protocols");

            migrationBuilder.DropIndex(
                name: "ix_review_job_protocols_thread_pass_job_id",
                table: "review_job_protocols");

            migrationBuilder.DropCheckConstraint(
                name: "ck_review_job_protocols_single_owner",
                table: "review_job_protocols");

            migrationBuilder.DropColumn(
                name: "ai_connection_id",
                table: "thread_pass_jobs");

            migrationBuilder.DropColumn(
                name: "ai_model",
                table: "thread_pass_jobs");

            migrationBuilder.DropColumn(
                name: "budget_block_cap_kind",
                table: "thread_pass_jobs");

            migrationBuilder.DropColumn(
                name: "budget_block_scope",
                table: "thread_pass_jobs");

            migrationBuilder.DropColumn(
                name: "budget_block_spent_usd",
                table: "thread_pass_jobs");

            migrationBuilder.DropColumn(
                name: "budget_block_threshold_usd",
                table: "thread_pass_jobs");

            migrationBuilder.DropColumn(
                name: "cost_is_approximate",
                table: "thread_pass_jobs");

            migrationBuilder.DropColumn(
                name: "total_estimated_cost_usd",
                table: "thread_pass_jobs");

            migrationBuilder.DropColumn(
                name: "total_input_tokens",
                table: "thread_pass_jobs");

            migrationBuilder.DropColumn(
                name: "total_output_tokens",
                table: "thread_pass_jobs");

            migrationBuilder.DropColumn(
                name: "thread_pass_job_id",
                table: "review_job_protocols");

            migrationBuilder.AlterColumn<Guid>(
                name: "job_id",
                table: "review_job_protocols",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
