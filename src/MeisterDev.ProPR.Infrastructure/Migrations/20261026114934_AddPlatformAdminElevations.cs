using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformAdminElevations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "elevation_id",
                table: "tenant_audit_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "platform_admin_elevations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    administrator_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    administrator_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ended_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    concurrency_stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_admin_elevations", x => x.id);
                    table.ForeignKey(
                        name: "FK_platform_admin_elevations_app_users_administrator_user_id",
                        column: x => x.administrator_user_id,
                        principalTable: "app_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_platform_admin_elevations_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_platform_admin_elevations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_audit_entries_elevation_occurred_at",
                table: "tenant_audit_entries",
                columns: new[] { "elevation_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_platform_admin_elevations_administrator_expires_at",
                table: "platform_admin_elevations",
                columns: new[] { "administrator_user_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_platform_admin_elevations_client_id",
                table: "platform_admin_elevations",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_platform_admin_elevations_open_expires_at",
                table: "platform_admin_elevations",
                column: "expires_at",
                filter: "ended_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_platform_admin_elevations_tenant_started_at",
                table: "platform_admin_elevations",
                columns: new[] { "tenant_id", "started_at" },
                descending: new[] { false, true });

            migrationBuilder.AddForeignKey(
                name: "FK_tenant_audit_entries_platform_admin_elevations_elevation_id",
                table: "tenant_audit_entries",
                column: "elevation_id",
                principalTable: "platform_admin_elevations",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tenant_audit_entries_platform_admin_elevations_elevation_id",
                table: "tenant_audit_entries");

            migrationBuilder.DropTable(
                name: "platform_admin_elevations");

            migrationBuilder.DropIndex(
                name: "ix_tenant_audit_entries_elevation_occurred_at",
                table: "tenant_audit_entries");

            migrationBuilder.DropColumn(
                name: "elevation_id",
                table: "tenant_audit_entries");
        }
    }
}
