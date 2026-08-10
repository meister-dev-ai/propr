// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.TestSupport;

/// <summary>
///     Seeds the rows a migration test needs at a historical schema point.
///     <para>
///         Written as raw SQL naming only long-standing columns, and deliberately not through the entity
///         model. A migration test migrates the database to some earlier point and then inserts; the entity
///         model is always at HEAD, so an insert through it names every column the product has today and
///         fails on the first one that migration had not added yet. That failure looks like a broken
///         migration test but is really just today's schema leaking backwards, and it lands on whoever next
///         adds a column rather than on whoever wrote the test.
///     </para>
/// </summary>
public static class HistoricalSchemaSeed
{
    /// <summary>
    ///     Inserts a tenant and a client, returning the client's id. Both carry only the columns that have
    ///     existed since well before any migration these tests target.
    /// </summary>
    /// <param name="dbContext">A context connected to the database under test.</param>
    /// <param name="name">Human-readable name, so a failure names which test seeded the row.</param>
    /// <param name="ct">The cancellation token.</param>
    public static async Task<Guid> SeedClientAsync(DbContext dbContext, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var tenantId = TenantCatalog.SystemTenantId;
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO tenants (id, slug, display_name, is_active, created_at, updated_at)
            VALUES ({0}, {1}, {2}, true, now(), now())
            ON CONFLICT (id) DO NOTHING
            """,
            [tenantId, $"seed-{tenantId:N}"[..32], name],
            ct);

        var clientId = Guid.NewGuid();
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO clients (id, tenant_id, display_name, is_active, created_at)
            VALUES ({0}, {1}, {2}, true, now())
            """,
            [clientId, tenantId, name],
            ct);

        return clientId;
    }
}
