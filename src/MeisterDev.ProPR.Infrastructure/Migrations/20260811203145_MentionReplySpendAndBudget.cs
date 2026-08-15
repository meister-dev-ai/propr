using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MentionReplySpendAndBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_review_job_protocols_single_owner",
                table: "review_job_protocols");

            migrationBuilder.AddColumn<Guid>(
                name: "mention_reply_job_id",
                table: "review_job_protocols",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ai_connection_id",
                table: "mention_reply_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_model",
                table: "mention_reply_jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "budget_block_cap_kind",
                table: "mention_reply_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "budget_block_scope",
                table: "mention_reply_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "budget_block_spent_usd",
                table: "mention_reply_jobs",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "budget_block_threshold_usd",
                table: "mention_reply_jobs",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "cost_is_approximate",
                table: "mention_reply_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "iteration_id",
                table: "mention_reply_jobs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "total_estimated_cost_usd",
                table: "mention_reply_jobs",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "total_input_tokens",
                table: "mention_reply_jobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "total_output_tokens",
                table: "mention_reply_jobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "ix_review_job_protocols_mention_reply_job_id",
                table: "review_job_protocols",
                column: "mention_reply_job_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_review_job_protocols_single_owner",
                table: "review_job_protocols",
                sql: "(job_id IS NOT NULL)::int + (thread_pass_job_id IS NOT NULL)::int + (mention_reply_job_id IS NOT NULL)::int = 1");

            migrationBuilder.CreateIndex(
                name: "ix_mention_reply_jobs_client_repo_pr",
                table: "mention_reply_jobs",
                columns: new[] { "client_id", "repository_id", "pull_request_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_review_job_protocols_mention_reply_jobs_mention_reply_job_id",
                table: "review_job_protocols",
                column: "mention_reply_job_id",
                principalTable: "mention_reply_jobs",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_review_job_protocols_mention_reply_jobs_mention_reply_job_id",
                table: "review_job_protocols");

            migrationBuilder.DropIndex(
                name: "ix_review_job_protocols_mention_reply_job_id",
                table: "review_job_protocols");

            migrationBuilder.DropCheckConstraint(
                name: "ck_review_job_protocols_single_owner",
                table: "review_job_protocols");

            migrationBuilder.DropIndex(
                name: "ix_mention_reply_jobs_client_repo_pr",
                table: "mention_reply_jobs");

            // A mention-owned trace row loses its only owner when the column goes, which both leaves it
            // unreachable from every read path and violates the two-owner constraint restored below. Removing
            // the rows is what makes this rollback run at all on an installation that has answered a mention.
            //
            // This is destructive and rolling forward again does not bring the rows back: the trace of what
            // each mention was asked and what it cost is gone, and its protocol events go with it by cascade.
            // The spend already recorded on mention_reply_jobs and on the client's daily usage samples is not
            // touched, so no money disappears from any total. Take a dump first if the traces matter.
            migrationBuilder.Sql("DELETE FROM review_job_protocols WHERE mention_reply_job_id IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "mention_reply_job_id",
                table: "review_job_protocols");

            migrationBuilder.DropColumn(
                name: "ai_connection_id",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "ai_model",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "budget_block_cap_kind",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "budget_block_scope",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "budget_block_spent_usd",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "budget_block_threshold_usd",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "cost_is_approximate",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "iteration_id",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "total_estimated_cost_usd",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "total_input_tokens",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "total_output_tokens",
                table: "mention_reply_jobs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_review_job_protocols_single_owner",
                table: "review_job_protocols",
                sql: "(job_id IS NULL) <> (thread_pass_job_id IS NULL)");
        }
    }
}
