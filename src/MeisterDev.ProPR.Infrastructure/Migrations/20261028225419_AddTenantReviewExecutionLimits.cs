using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantReviewExecutionLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_concurrent_reviews",
                table: "tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_review_duration_seconds",
                table: "tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "completion_reason",
                table: "review_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "max_concurrent_reviews",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "max_review_duration_seconds",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "completion_reason",
                table: "review_jobs");
        }
    }
}
