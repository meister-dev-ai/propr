// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using MeisterDev.ProPR.Application.Features.Providers.Identity;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

internal static class CodeInsightProviderScopeLookup
{
    internal static async Task<string> ResolveMissProviderScopeAsync(
        MeisterProPRDbContext db, Guid clientId, Guid? connectionId, CancellationToken ct, string? providerScope = null)
    {
        if (providerScope is not null)
        {
            return providerScope;
        }

        var connection = await db.ClientScmConnections.Where(row => row.Id == connectionId && row.ClientId == clientId)
            .Select(row => new
            {
                row.Provider,
                row.HostBaseUrl
            }).FirstOrDefaultAsync(ct);
        return connection is null ? string.Empty : ProviderSourceIdentity.FromConfiguredHost(connection.Provider, connection.HostBaseUrl).Value;
    }
}
