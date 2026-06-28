// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewJobResultSummary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "result_summary",
                table: "review_jobs",
                type: "text",
                nullable: true);

            // Backfill existing rows. The result is serialized with default options (PascalCase keys),
            // not the web camelCase settings, so the summary key is "Summary".
            migrationBuilder.Sql(
                "UPDATE review_jobs SET result_summary = result_json ->> 'Summary' WHERE result_json IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "result_summary",
                table: "review_jobs");
        }
    }
}
