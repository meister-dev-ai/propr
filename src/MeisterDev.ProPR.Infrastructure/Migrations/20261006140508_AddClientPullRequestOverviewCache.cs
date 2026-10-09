using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientPullRequestOverviewCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_pull_request_overview_cache",
                columns: table => new
                {
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    content = table.Column<string>(type: "text", nullable: true),
                    content_bytes = table.Column<int>(type: "integer", nullable: false),
                    failure = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    owner = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_refresh_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_pull_request_overview_cache", x => new { x.client_id, x.key });
                });

            migrationBuilder.CreateIndex(
                name: "IX_client_pull_request_overview_cache_client_id_connection_id_~",
                table: "client_pull_request_overview_cache",
                columns: new[] { "client_id", "connection_id", "failure", "next_refresh_at" });

            migrationBuilder.CreateIndex(
                name: "IX_client_pull_request_overview_cache_client_id_kind_expires_at",
                table: "client_pull_request_overview_cache",
                columns: new[] { "client_id", "kind", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_client_pull_request_overview_cache_client_id_source_key_kin~",
                table: "client_pull_request_overview_cache",
                columns: new[] { "client_id", "source_key", "kind", "failure", "next_refresh_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_pull_request_overview_cache");
        }
    }
}
