using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueCrawlConfigConstraint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_crawl_configurations_unique_config",
                table: "crawl_configurations",
                columns: new[] { "client_id", "organization_url", "project_id", "reviewer_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_crawl_configurations_unique_config",
                table: "crawl_configurations");
        }
    }
}
