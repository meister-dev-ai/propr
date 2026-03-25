using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IncreaseProtocolEventTextColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "output_summary",
                table: "protocol_events",
                type: "character varying(50000)",
                maxLength: 50000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "input_text_sample",
                table: "protocol_events",
                type: "character varying(50000)",
                maxLength: 50000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "output_summary",
                table: "protocol_events",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50000)",
                oldMaxLength: 50000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "input_text_sample",
                table: "protocol_events",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(50000)",
                oldMaxLength: 50000,
                oldNullable: true);
        }
    }
}
