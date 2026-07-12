using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPatTokenLookupHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "token_lookup_hash",
                table: "user_pats",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_pats_token_lookup_hash",
                table: "user_pats",
                column: "token_lookup_hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_pats_token_lookup_hash",
                table: "user_pats");

            migrationBuilder.DropColumn(
                name: "token_lookup_hash",
                table: "user_pats");
        }
    }
}
