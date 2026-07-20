using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientBudgetCaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "increment_budget_hard_cap_usd",
                table: "clients",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "monthly_budget_hard_cap_usd",
                table: "clients",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "monthly_budget_soft_cap_usd",
                table: "clients",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "pull_request_budget_hard_cap_usd",
                table: "clients",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "pull_request_budget_soft_cap_usd",
                table: "clients",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "increment_budget_hard_cap_usd",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "monthly_budget_hard_cap_usd",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "monthly_budget_soft_cap_usd",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "pull_request_budget_hard_cap_usd",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "pull_request_budget_soft_cap_usd",
                table: "clients");
        }
    }
}
