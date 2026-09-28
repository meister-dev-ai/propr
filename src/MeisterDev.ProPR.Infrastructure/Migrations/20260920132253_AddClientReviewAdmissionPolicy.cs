using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientReviewAdmissionPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "admission_max_changed_files",
                table: "clients",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "admission_max_changed_lines",
                table: "clients",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "admission_max_diff_bytes",
                table: "clients",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "admission_max_repository_megabytes",
                table: "clients",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "admission_max_reviews_per_pull_request_per_hour",
                table: "clients",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "admission_max_changed_files",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "admission_max_changed_lines",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "admission_max_diff_bytes",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "admission_max_repository_megabytes",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "admission_max_reviews_per_pull_request_per_hour",
                table: "clients");
        }
    }
}
