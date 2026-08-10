using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReviewJobExecutionLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_heartbeat_at",
                table: "review_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_expires_at",
                table: "review_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "lease_generation",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "lease_owner",
                table: "review_jobs",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_jobs_claim_candidates",
                table: "review_jobs",
                columns: new[] { "status", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_review_jobs_lease_expiry",
                table: "review_jobs",
                columns: new[] { "status", "lease_expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_review_jobs_claim_candidates",
                table: "review_jobs");

            migrationBuilder.DropIndex(
                name: "ix_review_jobs_lease_expiry",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "last_heartbeat_at",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "lease_expires_at",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "lease_generation",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "lease_owner",
                table: "review_jobs");
        }
    }
}
