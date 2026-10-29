// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Data;

/// <summary>
///     The clients whose tenant has as many review jobs processing as its cap on concurrent reviews allows.
/// </summary>
internal static class TenantReviewCapQueries
{
    /// <summary>
    ///     Returns the identifiers of the clients whose tenant sets a cap on concurrent reviews and has at least that
    ///     many review jobs processing. Thread passes and mention replies are stored apart and are not counted.
    /// </summary>
    /// <remarks>
    ///     A candidate query leaves the jobs of these clients out, so the backlog of a full tenant does not take the
    ///     places of other tenants' jobs. The result is a snapshot. The claim counts again under the tenant's lock
    ///     and decides.
    /// </remarks>
    public static IQueryable<Guid> ClientIdsAtTenantReviewCap(this MeisterProPRDbContext database) =>
        database.Clients
            .Where(client => client.Tenant!.MaxConcurrentReviews != null
                             && database.ReviewJobs.Count(job =>
                                 job.Status == JobStatus.Processing
                                 && database.Clients.Any(other => other.Id == job.ClientId && other.TenantId == client.TenantId))
                             >= client.Tenant.MaxConcurrentReviews)
            .Select(client => client.Id);
}
