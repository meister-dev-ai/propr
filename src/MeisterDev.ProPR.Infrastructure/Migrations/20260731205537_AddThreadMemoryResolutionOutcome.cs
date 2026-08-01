using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddThreadMemoryResolutionOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "resolution_clarity",
                table: "thread_memory_records",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "resolution_intent",
                table: "thread_memory_records",
                type: "smallint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "resolution_clarity",
                table: "thread_memory_records");

            migrationBuilder.DropColumn(
                name: "resolution_intent",
                table: "thread_memory_records");
        }
    }
}
