// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    [DbContext(typeof(MeisterProPRDbContext))]
    [Migration("20260424223000_RepairMissingClientReviewerIdColumn")]
    public sealed class RepairMissingClientReviewerIdColumn : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Shared local databases can pick up a later migration from a different checkout that
            // dropped clients.reviewer_id. This branch still reads that legacy column, so restore it
            // idempotently and recover the Azure DevOps reviewer GUID from stored reviewer identities.
            migrationBuilder.Sql(
                """
                ALTER TABLE clients
                ADD COLUMN IF NOT EXISTS reviewer_id uuid;
                """);

            migrationBuilder.Sql(
                """
                UPDATE clients AS c
                SET reviewer_id = source.reviewer_id
                FROM (
                    SELECT DISTINCT ON (cri.client_id)
                        cri.client_id,
                        cri.external_user_id::uuid AS reviewer_id
                    FROM client_reviewer_identities AS cri
                    WHERE cri.provider = 0
                      AND cri.external_user_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                    ORDER BY cri.client_id, cri.updated_at DESC, cri.id DESC
                ) AS source
                WHERE c.id = source.client_id
                  AND c.reviewer_id IS NULL;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE clients
                DROP COLUMN IF EXISTS reviewer_id;
                """);
        }
    }
}
