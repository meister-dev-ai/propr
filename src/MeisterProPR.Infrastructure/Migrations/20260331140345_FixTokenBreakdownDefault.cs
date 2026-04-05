// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixTokenBreakdownDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "token_breakdown",
                table: "review_jobs",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'",
                oldClrType: typeof(string),
                oldType: "jsonb");

            // Backfill rows that have a JSON string ("") instead of a JSON array ([])
            migrationBuilder.Sql(
                "UPDATE review_jobs SET token_breakdown = '[]'::jsonb WHERE jsonb_typeof(token_breakdown) <> 'array';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "token_breakdown",
                table: "review_jobs",
                type: "jsonb",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldDefaultValueSql: "'[]'");
        }
    }
}
