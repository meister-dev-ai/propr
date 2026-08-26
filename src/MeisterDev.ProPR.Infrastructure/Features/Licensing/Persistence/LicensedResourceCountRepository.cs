// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>
///     Counts what the installation currently holds for the dimensions a license states limits for.
///     <para>
///         Each read is a single count, taken when the administration surface asks for it, from the same query
///         a creation site is admitted against. Nothing is cached and nothing is recorded. The job count is
///         served by the index on the status column; the runner index is led by the tenant, so an
///         installation-wide count of usable runners does not use it, which is acceptable for a table sized
///         by the hosts an operator runs.
///     </para>
/// </summary>
/// <param name="dbContext">The context the counts are taken on.</param>
/// <param name="timeProvider">The clock a runner credential is judged expired against.</param>
/// <param name="licensingCapabilityService">
///     Answers whether multi-tenancy is available, which decides which clients the installation holds.
/// </param>
public sealed class LicensedResourceCountRepository(
    MeisterProPRDbContext dbContext,
    TimeProvider timeProvider,
    ILicensingCapabilityService? licensingCapabilityService = null) : ILicensedResourceCountSource
{
    /// <inheritdoc />
    public async Task<LicensedResourceCounts> GetCountsAsync(CancellationToken cancellationToken = default)
    {
        var clients = await LicensedResourceCountQueries
            .CountClientsAsync(dbContext, licensingCapabilityService, cancellationToken)
            .ConfigureAwait(false);

        var enrolledRunners = await LicensedResourceCountQueries
            .CountEnrolledRunnersAsync(dbContext, timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        var reviewsInProgress = await LicensedResourceCountQueries
            .CountReviewsInProgressAsync(dbContext, cancellationToken)
            .ConfigureAwait(false);

        return new LicensedResourceCounts(clients, enrolledRunners, reviewsInProgress);
    }
}
