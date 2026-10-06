// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RetainCodeInsightThreadEligibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "code_insight_thread_eligibility",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeInsightPullRequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderScope = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ProviderThreadId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IdentityFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExcludedFromHumanMisses = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_thread_eligibility", x => x.Id);
                    table.ForeignKey(
                        name: "FK_code_insight_thread_eligibility_code_insight_pull_requests_~",
                        column: x => x.CodeInsightPullRequestId,
                        principalTable: "code_insight_pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_code_insight_thread_eligibility_CodeInsightPullRequestId",
                table: "code_insight_thread_eligibility",
                column: "CodeInsightPullRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_code_insight_thread_eligibility_IdentityFingerprint",
                table: "code_insight_thread_eligibility",
                column: "IdentityFingerprint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_insight_thread_eligibility");
        }
    }
}
