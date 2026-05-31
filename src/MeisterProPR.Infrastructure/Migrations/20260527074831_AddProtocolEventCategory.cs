using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProtocolEventCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "event_category",
                table: "protocol_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_protocol_events_event_category",
                table: "protocol_events",
                column: "event_category");

            migrationBuilder.CreateIndex(
                name: "ix_protocol_events_occurred_at",
                table: "protocol_events",
                column: "occurred_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_protocol_events_event_category",
                table: "protocol_events");

            migrationBuilder.DropIndex(
                name: "ix_protocol_events_occurred_at",
                table: "protocol_events");

            migrationBuilder.DropColumn(
                name: "event_category",
                table: "protocol_events");
        }
    }
}
