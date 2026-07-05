using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClientReviewPasses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "client_review_passes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    configured_model_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_client_review_passes", x => x.id);
                    table.ForeignKey(
                        name: "FK_client_review_passes_ai_configured_models_configured_model_~",
                        column: x => x.configured_model_id,
                        principalTable: "ai_configured_models",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_client_review_passes_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_client_review_passes_client_id_ordinal",
                table: "client_review_passes",
                columns: new[] { "client_id", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_client_review_passes_configured_model_id",
                table: "client_review_passes",
                column: "configured_model_id");

            // Defensive cleanup for databases that carried the retired numeric pass-count model. The
            // reviewUnionPass AI purpose was removed together with the review-pass list, so any leftover binding
            // rows for it would fail to load once the enum member is gone (the purpose no longer parses) — drop
            // them. Matched case-insensitively because bindings persist the purpose via Enum.ToString().
            migrationBuilder.Sql("DELETE FROM ai_purpose_bindings WHERE lower(purpose) = 'reviewunionpass';");

            // The multi_pass_union_pass_count column was replaced by the client_review_passes table. Drop it with
            // a guarded raw statement (not a model DropColumn) because a fresh database created from this history
            // never had the column — IF EXISTS makes the migration a no-op there while still reconciling databases
            // that were provisioned with the retired column.
            migrationBuilder.Sql("ALTER TABLE clients DROP COLUMN IF EXISTS multi_pass_union_pass_count;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_review_passes");
        }
    }
}
