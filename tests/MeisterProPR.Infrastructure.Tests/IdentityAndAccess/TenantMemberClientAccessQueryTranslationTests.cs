// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Tests.IdentityAndAccess;

/// <summary>
///     Guards that the member client-access read query stays translatable by the relational (Npgsql)
///     provider. The service tests run on the in-memory provider, which happily evaluates expressions the
///     real provider rejects (e.g. ordering by a projected DTO member), so this check must mirror the query
///     shape used by <c>TenantMemberClientAccessService.ListMemberAccessAsync</c> and be kept in sync with it.
///     <c>ToQueryString()</c> throws the same "could not be translated" exception the runtime would hit; it
///     needs no live database connection.
/// </summary>
public sealed class TenantMemberClientAccessQueryTranslationTests
{
    [Fact]
    public void ListMemberAccessQuery_IsTranslatableByNpgsql()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql("Host=localhost;Database=x;Username=x;Password=x", o => o.UseVector())
            .Options;
        using var db = new MeisterProPRDbContext(options);

        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var query = db.UserClientRoles
            .AsNoTracking()
            .Where(role => role.UserId == userId)
            .Join(
                db.Clients.Where(client => client.TenantId == tenantId),
                role => role.ClientId,
                client => client.Id,
                (role, client) => new { role, client })
            .OrderBy(pair => pair.client.DisplayName)
            .Select(pair => new TenantMemberClientAccessDto(
                pair.client.Id,
                pair.client.DisplayName,
                pair.role.Role,
                pair.role.AssignedAt));

        var sql = query.ToQueryString();

        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
    }
}
