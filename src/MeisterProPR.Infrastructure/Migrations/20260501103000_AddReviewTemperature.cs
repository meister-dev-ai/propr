// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    [DbContext(typeof(MeisterProPRDbContext))]
    [Migration("20260501103000_AddReviewTemperature")]
    public sealed class AddReviewTemperature : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "review_temperature",
                table: "crawl_configurations",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "review_temperature",
                table: "review_jobs",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "review_temperature",
                table: "webhook_configurations",
                type: "real",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "review_temperature",
                table: "crawl_configurations");

            migrationBuilder.DropColumn(
                name: "review_temperature",
                table: "review_jobs");

            migrationBuilder.DropColumn(
                name: "review_temperature",
                table: "webhook_configurations");
        }
    }
}
