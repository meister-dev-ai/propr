// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>
///     What the installation holds for each dimension a license states limits for, as one definition per
///     dimension.
///     <para>
///         The reporting read and the admission decision at a creation site both count from here. Two counting
///         rules would let the number an administrator reads differ from the number a creation is refused
///         against, and an operator would see a refusal naming a number the panel does not show.
///     </para>
///     <para>
///         The count of executing reviews is reported from here and enforced inside the claim statement that
///         starts one, so that dimension has a second definition in SQL.
///     </para>
///     <para>
///         Each method is a single count, taken against whatever transaction the given context is in. Nothing
///         is cached and nothing is recorded, so removing a row lowers the count with no further action.
///     </para>
/// </summary>
internal static class LicensedResourceCountQueries
{
    /// <summary>
    ///     Counts the clients the installation holds. A client that is switched off is still one it holds.
    /// </summary>
    /// <remarks>
    ///     Counted over the clients the installation can list, patch and delete, which is the same visibility
    ///     rule <see cref="TenantCatalog.VisibleClients" /> states for those surfaces. Without
    ///     multi-tenancy a client outside the System tenant is not one of them, so counting it would hold the
    ///     installation to a number no action available to the operator can lower.
    /// </remarks>
    /// <param name="dbContext">The context to count on.</param>
    /// <param name="licensingCapabilityService">
    ///     Answers whether multi-tenancy is available. A host that registers none holds every client in scope,
    ///     the same default the client administration service applies.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The count.</returns>
    internal static async Task<long> CountClientsAsync(
        MeisterProPRDbContext dbContext,
        ILicensingCapabilityService? licensingCapabilityService,
        CancellationToken cancellationToken = default)
    {
        var multiTenancyAvailable = licensingCapabilityService is null
                                    || await licensingCapabilityService
                                        .IsEnabledAsync(PremiumCapabilityKey.MultiTenancy, cancellationToken)
                                        .ConfigureAwait(false);

        return await TenantCatalog
            .VisibleClients(dbContext.Clients.AsNoTracking(), multiTenancyAvailable)
            .LongCountAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Counts the runners the installation can still be given work by. A revoked runner is still a row, and
    ///     it can no longer be given work, so it is not one the installation holds against a runner limit. A
    ///     runner whose credential has expired is left out on the same rule: it can no longer authenticate, so
    ///     it can no longer lease, and the host behind it enrolls as a new row when it comes back.
    /// </summary>
    /// <param name="dbContext">The context to count on.</param>
    /// <param name="asOf">The instant a credential is judged expired against, from the host clock.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The count.</returns>
    internal static Task<long> CountEnrolledRunnersAsync(
        MeisterProPRDbContext dbContext,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default) =>
        dbContext.ReviewRunners
            .AsNoTracking()
            .LongCountAsync(
                runner => runner.State == RunnerState.Enrolled && runner.CredentialExpiresAt > asOf,
                cancellationToken);

    /// <summary>
    ///     Counts the reviews executing at this instant. Executing rather than queued: a limit on concurrent
    ///     reviews is about the ones running.
    /// </summary>
    /// <param name="dbContext">The context to count on.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The count.</returns>
    internal static Task<long> CountReviewsInProgressAsync(
        MeisterProPRDbContext dbContext,
        CancellationToken cancellationToken = default) =>
        dbContext.ReviewJobs
            .AsNoTracking()
            .LongCountAsync(job => job.Status == JobStatus.Processing, cancellationToken);
}
