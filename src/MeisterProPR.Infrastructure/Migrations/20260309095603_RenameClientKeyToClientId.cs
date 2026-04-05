// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameClientKeyToClientId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_review_jobs_client_key",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "client_key",
                table: "review_jobs");

            migrationBuilder.AddColumn<Guid>(
                name: "client_id",
                table: "review_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_jobs_client_id",
                table: "review_jobs",
                column: "client_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_review_jobs_client_id",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "client_id",
                table: "review_jobs");

            migrationBuilder.AddColumn<string>(
                name: "client_key",
                table: "review_jobs",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_jobs_client_key",
                table: "review_jobs",
                column: "client_key");
        }
    }
}
