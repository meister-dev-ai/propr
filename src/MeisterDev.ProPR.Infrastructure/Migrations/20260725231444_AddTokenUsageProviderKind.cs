using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenUsageProviderKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_client_token_usage_samples_unique",
                table: "client_token_usage_samples");

            migrationBuilder.AddColumn<string>(
                name: "provider_kind",
                table: "client_token_usage_samples",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_client_token_usage_samples_unique",
                table: "client_token_usage_samples",
                columns: new[] { "client_id", "model_id", "logical_model_name", "provider_kind", "date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_client_token_usage_samples_unique",
                table: "client_token_usage_samples");

            migrationBuilder.DropColumn(
                name: "provider_kind",
                table: "client_token_usage_samples");

            migrationBuilder.CreateIndex(
                name: "ix_client_token_usage_samples_unique",
                table: "client_token_usage_samples",
                columns: new[] { "client_id", "model_id", "logical_model_name", "date" },
                unique: true);
        }
    }
}
