using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CreateLicensingSystemProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "licensing_replica_hostnames",
                columns: table => new
                {
                    hostname = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_licensing_replica_hostnames", x => x.hostname);
                });

            migrationBuilder.CreateTable(
                name: "licensing_system_profile",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    stable_components = table.Column<string>(type: "jsonb", nullable: false),
                    volatile_components = table.Column<string>(type: "jsonb", nullable: false),
                    profile_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_licensing_system_profile", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "licensing_system_profile_drift",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_components = table.Column<string>(type: "jsonb", nullable: false),
                    previous_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    new_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_licensing_system_profile_drift", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_licensing_system_profile_drift_occurred_at",
                table: "licensing_system_profile_drift",
                column: "occurred_at",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "licensing_replica_hostnames");

            migrationBuilder.DropTable(
                name: "licensing_system_profile");

            migrationBuilder.DropTable(
                name: "licensing_system_profile_drift");
        }
    }
}
