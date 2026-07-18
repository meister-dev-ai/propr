using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProtocolEventCacheWriteAndReasoningTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "cache_write_tokens",
                table: "protocol_events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "reasoning_tokens",
                table: "protocol_events",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cache_write_tokens",
                table: "protocol_events");

            migrationBuilder.DropColumn(
                name: "reasoning_tokens",
                table: "protocol_events");
        }
    }
}
