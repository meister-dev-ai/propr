using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Adds the largest number of findings a client posts as comments on one pull request. The column default gives
    ///     every existing client the limit of 20 findings, and a client without a limit holds null.
    /// </summary>
    public partial class AddClientMaximumFindingsToPost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "maximum_findings_to_post",
                table: "clients",
                type: "integer",
                nullable: true,
                defaultValue: 20);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "maximum_findings_to_post",
                table: "clients");
        }
    }
}
