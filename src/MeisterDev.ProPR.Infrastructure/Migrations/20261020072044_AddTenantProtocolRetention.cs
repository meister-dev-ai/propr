using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantProtocolRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "protocol_retention_days",
                table: "tenants",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "protocol_retention_days",
                table: "tenants");
        }
    }
}
