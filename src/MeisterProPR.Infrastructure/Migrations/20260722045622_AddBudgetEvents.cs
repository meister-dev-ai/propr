using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budget_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<int>(type: "integer", nullable: false),
                    scope = table.Column<int>(type: "integer", nullable: false),
                    threshold_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    spent_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pull_request_id = table.Column<int>(type: "integer", nullable: true),
                    iteration_id = table.Column<int>(type: "integer", nullable: true),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_budget_events_client_id_occurred_at",
                table: "budget_events",
                columns: new[] { "client_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budget_events");
        }
    }
}
