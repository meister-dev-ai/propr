using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLogicalModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_logical_model_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    capability = table.Column<int>(type: "integer", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configured_model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reasoning_effort = table.Column<int>(type: "integer", nullable: false),
                    protocol_mode = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_logical_model_overrides", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_logical_models",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    capability = table.Column<int>(type: "integer", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    configured_model_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reasoning_effort = table.Column<int>(type: "integer", nullable: false),
                    protocol_mode = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_logical_models", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_logical_model_overrides_client_name",
                table: "ai_logical_model_overrides",
                columns: new[] { "client_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_logical_models_tenant_name",
                table: "ai_logical_models",
                columns: new[] { "tenant_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_logical_model_overrides");

            migrationBuilder.DropTable(
                name: "ai_logical_models");
        }
    }
}
