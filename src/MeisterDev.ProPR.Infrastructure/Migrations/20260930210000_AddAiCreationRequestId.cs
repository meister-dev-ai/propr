using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations;

[DbContext(typeof(MeisterProPRDbContext))]
[Migration("20260930210000_AddAiCreationRequestId")]
public sealed class AddAiCreationRequestId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "creation_request_id",
            table: "ai_connection_profiles",
            type: "uuid",
            nullable: true);
        migrationBuilder.CreateIndex(
            name: "ux_ai_connection_profiles_client_creation_request",
            table: "ai_connection_profiles",
            columns: ["client_id", "creation_request_id"],
            unique: true,
            filter: "creation_request_id IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_ai_connection_profiles_client_creation_request",
            table: "ai_connection_profiles");
        migrationBuilder.DropColumn(
            name: "creation_request_id",
            table: "ai_connection_profiles");
    }
}
