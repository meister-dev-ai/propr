// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiConnectionModelCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "model_category",
                table: "ai_connections",
                type: "smallint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_connections_client_id_model_category",
                table: "ai_connections",
                columns: new[] { "client_id", "model_category" },
                unique: true,
                filter: "model_category IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ai_connections_client_id_model_category",
                table: "ai_connections");

            migrationBuilder.DropColumn(
                name: "model_category",
                table: "ai_connections");
        }
    }
}
