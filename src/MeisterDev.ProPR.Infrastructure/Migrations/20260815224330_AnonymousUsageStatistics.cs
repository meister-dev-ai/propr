using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AnonymousUsageStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "usage_statistics_identity",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_statistics_identity", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "usage_statistics_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    community_opt_in = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    consent_gate_satisfied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notice_dismissed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_succeeded = table.Column<bool>(type: "boolean", nullable: true),
                    last_attempt_detail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    latest_version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    advisories_json = table.Column<string>(type: "text", nullable: true),
                    update_information_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_statistics_settings", x => x.id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_statistics_identity");

            migrationBuilder.DropTable(
                name: "usage_statistics_settings");
        }
    }
}
