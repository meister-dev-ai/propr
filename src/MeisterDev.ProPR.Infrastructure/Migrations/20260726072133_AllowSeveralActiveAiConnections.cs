// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AllowSeveralActiveAiConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ai_connection_profiles_client_id_active",
                table: "ai_connection_profiles");

            migrationBuilder.CreateIndex(
                name: "ix_ai_connection_profiles_client_id_active",
                table: "ai_connection_profiles",
                column: "client_id",
                filter: "is_active = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_ai_connection_profiles_client_id_active",
                table: "ai_connection_profiles");

            migrationBuilder.CreateIndex(
                name: "ix_ai_connection_profiles_client_id_active",
                table: "ai_connection_profiles",
                column: "client_id",
                unique: true,
                filter: "is_active = true");
        }
    }
}
