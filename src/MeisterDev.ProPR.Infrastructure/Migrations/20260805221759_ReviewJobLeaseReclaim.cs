using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReviewJobLeaseReclaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "consecutive_reclaim_count",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "failure_reason",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_reclaimed_at",
                table: "review_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "publishing_started_at",
                table: "review_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "total_reclaim_count",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "consecutive_reclaim_count",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "failure_reason",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "last_reclaimed_at",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "publishing_started_at",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "total_reclaim_count",
                table: "review_jobs");
        }
    }
}
