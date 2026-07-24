using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AiConnectionTenantScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "client_id",
                table: "ai_connection_profiles",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                table: "ai_connection_profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_connection_profiles_tenant_id_display_name",
                table: "ai_connection_profiles",
                columns: new[] { "tenant_id", "display_name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ai_connection_profiles_tenants_tenant_id",
                table: "ai_connection_profiles",
                column: "tenant_id",
                principalTable: "tenants",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ai_connection_profiles_tenants_tenant_id",
                table: "ai_connection_profiles");

            migrationBuilder.DropIndex(
                name: "ix_ai_connection_profiles_tenant_id_display_name",
                table: "ai_connection_profiles");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                table: "ai_connection_profiles");

            migrationBuilder.AlterColumn<Guid>(
                name: "client_id",
                table: "ai_connection_profiles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
