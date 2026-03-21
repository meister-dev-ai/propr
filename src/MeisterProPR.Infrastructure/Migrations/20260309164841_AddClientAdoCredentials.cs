using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientAdoCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ado_client_id",
                table: "clients",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ado_client_secret",
                table: "clients",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ado_tenant_id",
                table: "clients",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ado_client_id",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "ado_client_secret",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "ado_tenant_id",
                table: "clients");
        }
    }
}
