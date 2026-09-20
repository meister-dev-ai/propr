// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Whether one administrator still holds the role a connection's owner requires.
/// </summary>
/// <remarks>
///     <para>
///         The dispatch route derives the same rule from the request, which is the check that refuses before an
///         add-in is invoked. This reads it from the database instead, because the check that matters a second
///         time happens where there is no request: an invocation outlives the call that opened it, a credential
///         the action produced is stored minutes later, and a role can be withdrawn while the administrator is
///         at the provider.
///     </para>
///     <para>
///         The rule is the one the connection's owner states. A client-owned connection needs client
///         administrator on that client, which a tenant administrator of that client's tenant also holds; a
///         tenant-owned connection needs tenant administrator on that tenant; and a connection with neither owner
///         is reachable only by a platform administrator.
///     </para>
/// </remarks>
public interface IProviderConnectionOwnerRoles
{
    /// <summary>Whether <paramref name="adminId" /> holds the role <paramref name="connection" />'s owner requires.</summary>
    /// <param name="connection">The connection the action acts on.</param>
    /// <param name="adminId">The administrator whose roles are read, or null when none was recorded.</param>
    /// <param name="ct">Cancels the read.</param>
    Task<bool> HoldsOwnerRoleAsync(AiConnectionDto connection, Guid? adminId, CancellationToken ct = default);

    /// <summary>
    ///     The same answer, read inside <paramref name="db" />'s transaction with the rows it depends on held.
    /// </summary>
    /// <remarks>
    ///     Used where the answer authorizes a write in that transaction. The rows the decision reads are taken
    ///     with a share lock, so a revocation of one of them waits for the transaction to end: either it commits
    ///     first and this read sees it, or it waits and the write it would have invalidated has already happened.
    ///     Read through a separate connection, the answer would be stale the moment it was given.
    /// </remarks>
    /// <param name="db">The context whose transaction the write happens in.</param>
    /// <param name="connection">The connection the action acts on.</param>
    /// <param name="adminId">The administrator whose roles are read, or null when none was recorded.</param>
    /// <param name="ct">Cancels the read.</param>
    Task<bool> HoldsOwnerRoleAsync(
        MeisterProPRDbContext db,
        AiConnectionDto connection,
        Guid? adminId,
        CancellationToken ct = default);

    /// <summary>What the connection's owner requires, phrased for a refusal.</summary>
    /// <param name="connection">The connection the action acts on.</param>
    string DescribeRequirement(AiConnectionDto connection);

    /// <summary>
    ///     What one administrator is called, so a grant can record a name beside the identifier.
    /// </summary>
    /// <remarks>
    ///     Read once when the invocation is opened rather than when the grant is written, so a completion
    ///     arriving on a socket minutes later does not depend on the account still being readable.
    /// </remarks>
    /// <param name="adminId">The administrator to name, or null when none was recorded.</param>
    /// <param name="ct">Cancels the read.</param>
    Task<string?> DescribeAdministratorAsync(Guid? adminId, CancellationToken ct = default);
}

/// <inheritdoc />
/// <param name="contextFactory">Opens a context per call, independent of whatever built this.</param>
public sealed class ProviderConnectionOwnerRoles(IDbContextFactory<MeisterProPRDbContext> contextFactory)
    : IProviderConnectionOwnerRoles
{
    /// <inheritdoc />
    public async Task<bool> HoldsOwnerRoleAsync(
        AiConnectionDto connection,
        Guid? adminId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await this.ReadAsync(db, connection, adminId, hold: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> HoldsOwnerRoleAsync(
        MeisterProPRDbContext db,
        AiConnectionDto connection,
        Guid? adminId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(connection);

        return this.ReadAsync(db, connection, adminId, hold: true, ct);
    }

    /// <summary>Holds the rows one authorization decision reads until the caller's transaction ends.</summary>
    /// <remarks>
    ///     A share lock, not an exclusive one: several credential writes may hold the same administrator's rows at
    ///     once, and what has to wait is a revocation, which updates or deletes them. Rows that do not exist lock
    ///     nothing, which is the case where the answer is already no.
    /// </remarks>
    /// <param name="db">The context whose transaction holds the rows.</param>
    /// <param name="administrator">The administrator whose rows are held.</param>
    /// <param name="connection">The connection, which says which scoped rows matter.</param>
    /// <param name="ct">Cancels the statements.</param>
    private static async Task HoldAuthorizationRowsAsync(
        MeisterProPRDbContext db,
        Guid administrator,
        AiConnectionDto connection,
        CancellationToken ct)
    {
        await db.Database
            .ExecuteSqlRawAsync("SELECT id FROM app_users WHERE id = {0} FOR SHARE", [administrator], ct)
            .ConfigureAwait(false);

        await db.Database
            .ExecuteSqlRawAsync(
                "SELECT id FROM tenant_memberships WHERE user_id = {0} FOR SHARE",
                [administrator],
                ct)
            .ConfigureAwait(false);

        if (connection.ClientId is { } clientId)
        {
            await db.Database
                .ExecuteSqlRawAsync(
                    "SELECT id FROM user_client_roles WHERE user_id = {0} AND client_id = {1} FOR SHARE",
                    [administrator, clientId],
                    ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> ReadAsync(
        MeisterProPRDbContext db,
        AiConnectionDto connection,
        Guid? adminId,
        bool hold,
        CancellationToken ct)
    {
        if (adminId is not { } administrator)
        {
            return false;
        }

        if (hold)
        {
            await HoldAuthorizationRowsAsync(db, administrator, connection, ct).ConfigureAwait(false);
        }

        // A disabled account holds nothing. The sign-in paths refuse one already; this is the same rule applied
        // where there is no sign-in, which is where an account disabled mid-flow would otherwise still complete
        // one.
        var globalRole = await db.AppUsers
            .AsNoTracking()
            .Where(user => user.Id == administrator && user.IsActive)
            .Select(user => (AppUserRole?)user.GlobalRole)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (globalRole is null)
        {
            return false;
        }

        if (globalRole == AppUserRole.Admin)
        {
            return true;
        }

        if (connection.ClientId is { } clientId)
        {
            return await this.HoldsClientOwnerRoleAsync(db, clientId, administrator, ct).ConfigureAwait(false);
        }

        if (connection.TenantId is { } tenantId)
        {
            return await HoldsTenantAdministratorAsync(db, tenantId, administrator, ct).ConfigureAwait(false);
        }

        // Owned by neither a client nor a tenant, so there is no scoped role that could stand for it and the
        // platform administrator check above is the whole answer.
        return false;
    }

    /// <inheritdoc />
    public string DescribeRequirement(AiConnectionDto connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.ClientId is not null)
        {
            return "client administrator on the client that owns this connection";
        }

        return connection.TenantId is not null
            ? "tenant administrator on the tenant that owns this connection"
            : "platform administrator, because this connection is owned by neither a client nor a tenant";
    }

    /// <inheritdoc />
    public async Task<string?> DescribeAdministratorAsync(Guid? adminId, CancellationToken ct = default)
    {
        if (adminId is not { } administrator)
        {
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await db.AppUsers
            .AsNoTracking()
            .Where(user => user.Id == administrator)
            .Select(user => user.Username)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    // Compared for equality rather than by rank: both role columns are stored as the member's name, so an
    // ordering comparison is translated to a comparison between two strings, under which every role that sorts
    // after 'ClientAdministrator' or 'TenantAdministrator' would pass.
    private static Task<bool> HoldsTenantAdministratorAsync(
        MeisterProPRDbContext db,
        Guid tenantId,
        Guid administrator,
        CancellationToken ct)
    {
        return db.TenantMemberships
            .AsNoTracking()
            .AnyAsync(
                membership => membership.TenantId == tenantId
                              && membership.UserId == administrator
                              && membership.Role == TenantRole.TenantAdministrator,
                ct);
    }

    private async Task<bool> HoldsClientOwnerRoleAsync(
        MeisterProPRDbContext db,
        Guid clientId,
        Guid administrator,
        CancellationToken ct)
    {
        var owningTenant = await db.Clients
            .AsNoTracking()
            .Where(client => client.Id == clientId)
            .Select(client => (Guid?)client.TenantId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (owningTenant is not { } tenantId)
        {
            return false;
        }

        // A tenant administrator holds client administrator on every client of their tenant, which is how the
        // request path resolves the same caller's roles. Reading it the other way round here would refuse an
        // administrator the dispatch route had just admitted.
        if (await HoldsTenantAdministratorAsync(db, tenantId, administrator, ct).ConfigureAwait(false))
        {
            return true;
        }

        // An explicit assignment to a client inside a real tenant counts only while the administrator is still a
        // member of that tenant, which is the same qualification the request path applies when it resolves a
        // caller's client roles. Without it this check would admit an administrator the request path refuses.
        if (tenantId != Guid.Empty
            && !TenantCatalog.IsSystemTenant(tenantId)
            && !await db.TenantMemberships
                .AsNoTracking()
                .AnyAsync(
                    membership => membership.TenantId == tenantId && membership.UserId == administrator,
                    ct)
                .ConfigureAwait(false))
        {
            return false;
        }

        return await db.UserClientRoles
            .AsNoTracking()
            .AnyAsync(
                assignment => assignment.ClientId == clientId
                              && assignment.UserId == administrator
                              && assignment.Role == ClientRole.ClientAdministrator,
                ct)
            .ConfigureAwait(false);
    }
}
