using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantAiEndpointAllowList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "allowed_ai_endpoint_hosts",
                table: "tenants",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allowed_ai_endpoint_hosts",
                table: "tenants");
        }
    }
}
