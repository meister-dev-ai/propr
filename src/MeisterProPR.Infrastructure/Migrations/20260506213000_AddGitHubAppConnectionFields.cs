// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    [DbContext(typeof(MeisterProPRDbContext))]
    [Migration("20260506213000_AddGitHubAppConnectionFields")]
    public partial class AddGitHubAppConnectionFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "github_app_id",
                table: "client_scm_connections",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "github_app_installation_id",
                table: "client_scm_connections",
                type: "bigint",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "github_app_id",
                table: "client_scm_connections");

            migrationBuilder.DropColumn(
                name: "github_app_installation_id",
                table: "client_scm_connections");
        }
    }
}
