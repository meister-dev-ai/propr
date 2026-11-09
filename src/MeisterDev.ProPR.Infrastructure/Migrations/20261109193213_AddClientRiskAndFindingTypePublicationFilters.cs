using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <summary>
    ///     Adds two publication settings to every client: the lowest assessed risk a finding needs to be posted, and the
    ///     core finding types that are not posted. Existing clients receive the defaults, which post every finding.
    /// </summary>
    public partial class AddClientRiskAndFindingTypePublicationFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "minimum_risk_severity_to_post",
                table: "clients",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "withheld_finding_types",
                table: "clients",
                type: "text",
                nullable: false,
                defaultValueSql: "''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "minimum_risk_severity_to_post",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "withheld_finding_types",
                table: "clients");
        }
    }
}
