// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    [DbContext(typeof(MeisterProPRDbContext))]
    [Migration("20260424181000_RemoveLegacyClientReviewerId")]
    public partial class RemoveLegacyClientReviewerId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reviewer_id",
                table: "clients");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "reviewer_id",
                table: "clients",
                type: "uuid",
                nullable: true);
        }
    }
}
