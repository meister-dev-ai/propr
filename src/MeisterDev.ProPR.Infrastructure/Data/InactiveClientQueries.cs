// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data;

/// <summary>The clients that take in and run no work.</summary>
internal static class InactiveClientQueries
{
    /// <summary>Returns the identifiers of the clients that are deactivated or belong to a deactivated tenant.</summary>
    /// <remarks>
    ///     The intake takes in no work for these clients, and the work they already have waits until the client and
    ///     its tenant are active again. Work that runs when a client is deactivated finishes.
    /// </remarks>
    public static IQueryable<Guid> InactiveClientIds(this MeisterProPRDbContext database) =>
        database.Clients.Where(client => !client.IsActive || !client.Tenant!.IsActive).Select(client => client.Id);
}
