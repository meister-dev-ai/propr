// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewerIdToClients_RemoveFromCrawlConfigs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_crawl_configurations_unique_config",
                table: "crawl_configurations");

            migrationBuilder.DropColumn(
                name: "reviewer_id",
                table: "crawl_configurations");

            migrationBuilder.AddColumn<Guid>(
                name: "reviewer_id",
                table: "clients",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_crawl_configurations_unique_config",
                table: "crawl_configurations",
                columns: new[] { "client_id", "organization_url", "project_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_crawl_configurations_unique_config",
                table: "crawl_configurations");

            migrationBuilder.DropColumn(
                name: "reviewer_id",
                table: "clients");

            migrationBuilder.AddColumn<Guid>(
                name: "reviewer_id",
                table: "crawl_configurations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ix_crawl_configurations_unique_config",
                table: "crawl_configurations",
                columns: new[] { "client_id", "organization_url", "project_id", "reviewer_id" },
                unique: true);
        }
    }
}
