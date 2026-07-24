using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLogicalModelUsageAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_client_token_usage_samples_unique",
                table: "client_token_usage_samples");

            migrationBuilder.AddColumn<string>(
                name: "logical_model_name",
                table: "review_job_protocols",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "logical_model_name",
                table: "client_token_usage_samples",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "ix_client_token_usage_samples_unique",
                table: "client_token_usage_samples",
                columns: new[] { "client_id", "model_id", "logical_model_name", "date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_client_token_usage_samples_unique",
                table: "client_token_usage_samples");

            migrationBuilder.DropColumn(
                name: "logical_model_name",
                table: "review_job_protocols");

            migrationBuilder.DropColumn(
                name: "logical_model_name",
                table: "client_token_usage_samples");

            migrationBuilder.CreateIndex(
                name: "ix_client_token_usage_samples_unique",
                table: "client_token_usage_samples",
                columns: new[] { "client_id", "model_id", "date" },
                unique: true);
        }
    }
}
