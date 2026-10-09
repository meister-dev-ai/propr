// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations;

[DbContext(typeof(MeisterProPRDbContext))]
[Migration("20260930120000_WidenCodeInsightDimensionKey")]
public sealed class WidenCodeInsightDimensionKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "dimension_key",
            table: "code_insight_daily_counts",
            type: "character varying(512)",
            maxLength: 512,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(64)",
            oldMaxLength: 64);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM code_insight_daily_counts WHERE length(dimension_key) > 64) THEN
                    RAISE EXCEPTION 'Cannot narrow code_insight_daily_counts.dimension_key while keys exceed 64 characters';
                END IF;
            END;
            $$;
            """);
        migrationBuilder.AlterColumn<string>(
            name: "dimension_key",
            table: "code_insight_daily_counts",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(512)",
            oldMaxLength: 512);
    }
}
