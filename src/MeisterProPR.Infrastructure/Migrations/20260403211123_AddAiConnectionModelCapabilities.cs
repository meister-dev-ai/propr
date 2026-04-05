// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiConnectionModelCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_connection_model_capabilities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ai_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tokenizer_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    max_input_tokens = table.Column<int>(type: "integer", nullable: false),
                    embedding_dimensions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_connection_model_capabilities", x => x.id);
                    table.ForeignKey(
                        name: "FK_ai_connection_model_capabilities_ai_connections_ai_connecti~",
                        column: x => x.ai_connection_id,
                        principalTable: "ai_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_connection_model_capabilities_connection_model",
                table: "ai_connection_model_capabilities",
                columns: new[] { "ai_connection_id", "model_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_connection_model_capabilities");
        }
    }
}
