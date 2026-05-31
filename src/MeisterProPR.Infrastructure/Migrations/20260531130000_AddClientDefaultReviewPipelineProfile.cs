// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    [DbContext(typeof(MeisterProPRDbContext))]
    [Migration("20260531130000_AddClientDefaultReviewPipelineProfile")]
    public sealed class AddClientDefaultReviewPipelineProfile : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "default_review_pipeline_profile_id",
                table: "clients",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "default_review_pipeline_profile_updated_at_utc",
                table: "clients",
                type: "timestamp with time zone",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_review_pipeline_profile_id",
                table: "clients");

            migrationBuilder.DropColumn(
                name: "default_review_pipeline_profile_updated_at_utc",
                table: "clients");
        }
    }
}
