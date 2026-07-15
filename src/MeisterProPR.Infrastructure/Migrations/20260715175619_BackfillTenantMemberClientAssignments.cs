// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillTenantMemberClientAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Client access for regular members of real (non-System) tenants becomes assignment-driven:
            // membership alone no longer projects access onto every client in the tenant. Preserve each
            // existing member's current access by materializing an explicit ClientUser assignment for every
            // client currently in their tenant. Pre-existing explicit roles (e.g. ClientAdministrator) win
            // via ON CONFLICT DO NOTHING. Tenant administrators keep blanket access and are not backfilled;
            // the System tenant retains its blanket member projection and is excluded.
            migrationBuilder.Sql(
                """
                INSERT INTO user_client_roles (id, user_id, client_id, role, assigned_at)
                SELECT gen_random_uuid(), tm.user_id, c.id, 'ClientUser', now()
                FROM tenant_memberships tm
                JOIN clients c ON c.tenant_id = tm.tenant_id
                WHERE tm.role = 'TenantUser'
                  AND tm.tenant_id NOT IN ('11111111-1111-1111-1111-111111111111', '00000000-0000-0000-0000-000000000000')
                ON CONFLICT (user_id, client_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: this is a behavior-preserving data backfill. Backfilled assignments are
            // indistinguishable from ones a tenant administrator created afterwards, so they cannot be
            // reversed without risking removal of legitimate grants.
        }
    }
}
