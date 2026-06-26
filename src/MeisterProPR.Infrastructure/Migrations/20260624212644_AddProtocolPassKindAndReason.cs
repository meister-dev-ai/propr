using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProtocolPassKindAndReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "pass_kind",
                table: "review_job_protocols",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "review_job_protocols",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pass_kind",
                table: "review_job_protocols");

            migrationBuilder.DropColumn(
                name: "reason",
                table: "review_job_protocols");
        }
    }
}
