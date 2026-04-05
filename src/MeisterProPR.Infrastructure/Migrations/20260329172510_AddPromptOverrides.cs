// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "prompt_overrides",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    crawl_config_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope = table.Column<int>(type: "integer", nullable: false),
                    prompt_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    override_text = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prompt_overrides", x => x.id);
                    table.ForeignKey(
                        name: "FK_prompt_overrides_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_prompt_overrides_crawl_configurations_crawl_config_id",
                        column: x => x.crawl_config_id,
                        principalTable: "crawl_configurations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_prompt_overrides_client_id_scope_key",
                table: "prompt_overrides",
                columns: new[] { "client_id", "scope", "prompt_key" });

            migrationBuilder.CreateIndex(
                name: "ix_prompt_overrides_client_scope",
                table: "prompt_overrides",
                columns: new[] { "client_id", "prompt_key" },
                unique: true,
                filter: "crawl_config_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_prompt_overrides_crawl_config_id",
                table: "prompt_overrides",
                column: "crawl_config_id");

            migrationBuilder.CreateIndex(
                name: "ix_prompt_overrides_crawl_config_scope",
                table: "prompt_overrides",
                columns: new[] { "client_id", "crawl_config_id", "prompt_key" },
                unique: true,
                filter: "crawl_config_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "prompt_overrides");
        }
    }
}
