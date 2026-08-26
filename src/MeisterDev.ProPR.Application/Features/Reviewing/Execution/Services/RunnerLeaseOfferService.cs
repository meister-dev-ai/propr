// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Answers a runner asking for work.
///     <para>
///         The decision stays on the asking side. A runner asks only when it has a free slot, so the control
///         plane never has to maintain a view of who is busy, and a runner that stops responding removes its
///         own capacity from the pool by not asking again. What the control plane decides is which jobs this
///         particular runner is allowed to see, which is a correctness boundary rather than a preference.
///     </para>
///     <para>
///         Winning a candidate is the same conditional claim the in-process worker uses. Two runners handed
///         the same candidate resolve it in the database, so an offer never guarantees the job and never has
///         to be held anywhere.
///     </para>
/// </summary>
public sealed partial class RunnerLeaseOfferService(
    IRunnerRegistry runners,
    IRunnerLeaseOfferStore offers,
    IReviewJobLeaseStore leases,
    IRunnerJobDispatchPreparer preparer,
    IRunnerJobManifestResolver manifests,
    IOptions<ReviewLeaseOptions> leaseOptions,
    IRunnerJobBudgetRegistry budgets,
    IRunnerJobToolsRegistry tools,
    IRunnerWorkspaceRegistry workspaces,
    ILogger<RunnerLeaseOfferService> logger,
    IBudgetCapsProvider? budgetCaps = null,
    IReviewSpendAccumulator? spend = null,
    ILicenseLimitResolver? limits = null,
    IReviewJobExecutionStore? executionStore = null,
    IClientRegistry? clients = null,
    ILicensingCapabilityService? licensing = null) : IRunnerLeaseOfferService
{
    /// <inheritdoc />
    public async Task<RunnerLeaseOffer> OfferAsync(RunnerLeaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // One gate for every call site: the served window is already clamped by the manifest floor, so a
        // version refused here is refused by the heartbeat and the execution surface for the same reason,
        // with the same wording. Admitting a runner below the floor previously granted a lease whose
        // manifest the runner could not deserialize. The generation was bumped, the failure was not
        // named, and three such offers failed the job.
        if (!RunnerContractVersion.IsSupported(request.ContractVersion))
        {
            return RunnerLeaseOffer.Refuse(
                RunnerLeaseRefusal.UnsupportedContractVersion,
                RunnerContractVersion.DescribeMismatch(request.ContractVersion));
        }

        // A runner that asks with no free slot has drifted in its own slot accounting. Refusing is cheaper
        // than trusting the request, and the typed reason makes the drift visible instead of reporting an
        // empty queue.
        if (request.FreeSlots <= 0)
        {
            return RunnerLeaseOffer.Refuse(RunnerLeaseRefusal.NoFreeCapacity);
        }

        var runner = await runners.FindByIdAsync(request.RunnerId, ct).ConfigureAwait(false);
        if (runner is null || runner.State != RunnerState.Enrolled)
        {
            return RunnerLeaseOffer.Refuse(RunnerLeaseRefusal.RegistrationNotUsable);
        }

        // Checked on every offer rather than only at enrollment, because a license can lapse while a fleet is
        // enrolled and running. Without this an installation that lost the capability would keep serving work
        // to the runners it already has. A host composed without the licensing surface has no service here and
        // leases as before.
        if (licensing is not null
            && !await licensing.IsEnabledAsync(PremiumCapabilityKey.DistributedExecution, ct).ConfigureAwait(false))
        {
            LogNotLicensed(logger, runner.Id);
            return RunnerLeaseOffer.Refuse(
                RunnerLeaseRefusal.NotLicensed,
                "Distributed review execution is not licensed for this installation.");
        }

        // The same concurrency ceiling the in-process worker applies, resolved once per request so every
        // candidate below is judged against the same number. The ceiling holds across every host sharing the
        // database, which in a runner fleet is a different set from "on this host". The capped claim below
        // enforces it; the count here only avoids the candidate query when the answer is already visible. A
        // host composed without the licensing surface has no resolver and claims uncapped.
        var ceiling = limits is null
            ? null
            : ConcurrentReviewCeiling.From(await limits.ResolveAsync(LicenseLimitKey.ConcurrentReviews, ct).ConfigureAwait(false));
        if (ceiling is not null)
        {
            // A ceiling of zero admits no review at all, and the capped claim refuses a cap below one, so
            // the refusal is made here.
            if (ceiling.Cap == 0)
            {
                LogAtConcurrencyCeiling(logger, runner.Id, ceiling.Cap, null);
                return RunnerLeaseOffer.RefuseAtConcurrencyCeiling(ceiling.Cap, ceiling.Describe(null));
            }

            if (executionStore is not null)
            {
                var processing = await executionStore.CountProcessingJobsAsync(ct).ConfigureAwait(false);
                if (processing >= ceiling.Cap)
                {
                    LogAtConcurrencyCeiling(logger, runner.Id, ceiling.Cap, processing);
                    return RunnerLeaseOffer.RefuseAtConcurrencyCeiling(ceiling.Cap, ceiling.Describe(processing));
                }
            }
        }

        var candidates = await offers.GetOfferCandidatesAsync(
            runner.TenantId,
            runner.ClientScope,
            runner.Tags,
            leaseOptions.Value.ClaimCandidateLimit,
            ct).ConfigureAwait(false);

        var owner = runner.Id.ToString("D");
        var prWideByClient = new Dictionary<Guid, bool>();
        foreach (var job in candidates)
        {
            // A publishing pr_wide pass never dispatches, and the manifest resolver's refusal comes after
            // the claim and the mirror preparation. Skipping before the claim keeps that refusal from
            // repeating on every poll, each repetition costing a generation bump, a full repository
            // preparation and a release. The job stays claimable by the in-process worker, which runs it.
            if (await this.HasPublishingPrWidePassAsync(job.ClientId, prWideByClient, ct).ConfigureAwait(false))
            {
                LogSkippedPublishingPrWide(logger, job.Id, job.ClientId);
                continue;
            }

            ReviewJobLease? lease;
            if (ceiling is null)
            {
                lease = await leases
                    .TryClaimAsync(job.Id, owner, leaseOptions.Value.LeaseDuration, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                // The ceiling is carried by the claim itself. Counting first and claiming afterwards lets two
                // replicas claim two different jobs against the same count, and row locking does not
                // arbitrate that, because the rows differ.
                var capped = await leases.TryClaimWithinProcessingCapAsync(
                    job.Id,
                    owner,
                    leaseOptions.Value.LeaseDuration,
                    ceiling.Cap,
                    ct).ConfigureAwait(false);
                if (capped.Outcome == ReviewJobCappedClaimOutcome.AtCapacity)
                {
                    // No other candidate can be claimed either while the ceiling is full. The refusal quotes
                    // the numbers the claim measured rather than the ones read before it.
                    var measuredCap = capped.Cap ?? ceiling.Cap;
                    LogAtConcurrencyCeiling(logger, runner.Id, measuredCap, capped.ProcessingCount);
                    return RunnerLeaseOffer.RefuseAtConcurrencyCeiling(
                        measuredCap,
                        ceiling.Describe(capped.ProcessingCount));
                }

                lease = capped.Lease;
            }

            if (lease is null)
            {
                // Another owner claimed the job between the read and the claim. That is the normal outcome
                // under load rather than an error, so the next candidate is tried.
                continue;
            }

            // Held until ownership has actually been handed to the runner. Everything between the claim
            // and the grant can throw, and the request token is the runner's: a runner that disconnects
            // mid-preparation would otherwise leave the job Processing until its lease expired.
            var granted = false;
            try
            {
                var preparation = await preparer.PrepareAsync(job, lease, ct).ConfigureAwait(false);
                if (!preparation.Succeeded)
                {
                    // The job cannot be dispatched, for reasons unrelated to this runner. Releasing the
                    // lease means the failure does not also consume the runner's request.
                    LogDispatchPreparationFailed(logger, job.Id, preparation.Failure ?? "unknown");
                    continue;
                }

                var resolution = await manifests.ResolveAsync(preparation.Request!, ct).ConfigureAwait(false);
                if (!resolution.Succeeded)
                {
                    LogManifestResolutionFailed(logger, job.Id, resolution.Refusal ?? "unknown");
                    continue;
                }

                // Registered before the manifest is handed over, never after. The relay charges every
                // completion against this scope and refuses when it cannot find one, so a runner that
                // received its manifest first could make a call the control plane would have to turn away.
                budgets.Register(job.Id, await this.ResolveBudgetScopeAsync(job, ct).ConfigureAwait(false));

                LogLeaseGranted(logger, job.Id, runner.Id, lease.Generation);
                granted = true;
                return RunnerLeaseOffer.Grant(resolution.Manifest!);
            }
            finally
            {
                if (!granted)
                {
                    // Released on a token of its own. Using the request's token would let an aborted
                    // request cancel the cleanup that the abort requires. The workspace is released as
                    // well: an offer that stopped after preparation would otherwise leave two checkouts
                    // on disk per refusal, and no later path releases them.
                    budgets.Release(job.Id);
                    tools.Release(job.Id);
                    await workspaces.ReleaseAsync(job.Id).ConfigureAwait(false);
                    await leases.TryReleaseAsync(lease, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        return RunnerLeaseOffer.Refuse(RunnerLeaseRefusal.NoMatchingWork);
    }

    /// <summary>
    ///     The budget this job's relayed completions are charged against.
    ///     <para>
    ///         Always a scope, even for a client that configures no caps. The registry is what tells the
    ///         relay this replica is holding the job open. Returning null for an unconfigured client would
    ///         make "nothing to enforce" and "not this replica's job" the same answer, and every completion
    ///         for such a client would then be refused.
    ///     </para>
    ///     <para>
    ///         A scope built on <see cref="BudgetCaps.None" /> has nothing to trip, so an unconfigured
    ///         client is metered and never stopped, which is what the in-process path does.
    ///     </para>
    /// </summary>
    private async Task<BudgetScope> ResolveBudgetScopeAsync(ReviewJob job, CancellationToken ct)
    {
        if (budgetCaps is null || spend is null)
        {
            return new BudgetScope(BudgetCaps.None, EmptyBaseline);
        }

        var caps = await budgetCaps.GetCapsAsync(job.ClientId, ct);
        if (!caps.AnyConfigured)
        {
            // No baseline is read when nothing can be exceeded: the read costs a query per lease and
            // nothing downstream would compare against it.
            return new BudgetScope(caps, EmptyBaseline);
        }

        var baseline = await spend.GetBaselineAsync(
            ReviewSpendSubject.For(job),
            DateOnly.FromDateTime(DateTime.UtcNow),
            ct);

        return new BudgetScope(caps, baseline);
    }

    private static ReviewSpendBaseline EmptyBaseline { get; } =
        new(ReviewScopeSpend.None, ReviewScopeSpend.None, ReviewScopeSpend.None);

    /// <summary>
    ///     Whether the client's pass list has a publishing pr_wide entry, cached per offer: the same
    ///     client tends to fill a candidate window. Without a registry to ask, nothing is skipped here and
    ///     the manifest resolver's refusal remains the guarantee.
    /// </summary>
    private async Task<bool> HasPublishingPrWidePassAsync(
        Guid clientId,
        Dictionary<Guid, bool> cache,
        CancellationToken ct)
    {
        if (clients is null)
        {
            return false;
        }

        if (cache.TryGetValue(clientId, out var known))
        {
            return known;
        }

        var passes = await clients.GetReviewPassesAsync(clientId, ct);
        var hasPublishingPrWide = passes.Any(pass =>
            !pass.Shadow && string.Equals(pass.Scope, ReviewPassScope.PrWide, StringComparison.Ordinal));
        cache[clientId] = hasPublishingPrWide;
        return hasPublishingPrWide;
    }

    [LoggerMessage(EventId = 5401, Level = LogLevel.Information, Message = "Leased review job {JobId} to runner {RunnerId} at generation {Generation}")]
    private static partial void LogLeaseGranted(ILogger logger, Guid jobId, Guid runnerId, int generation);

    [LoggerMessage(
        EventId = 5402, Level = LogLevel.Warning, Message = "Review job {JobId} could not be prepared for dispatch and was returned to the queue: {Reason}")]
    private static partial void LogDispatchPreparationFailed(ILogger logger, Guid jobId, string reason);

    [LoggerMessage(
        EventId = 5403, Level = LogLevel.Warning, Message = "Review job {JobId} could not have a manifest resolved and was returned to the queue: {Reason}")]
    private static partial void LogManifestResolutionFailed(ILogger logger, Guid jobId, string reason);

    [LoggerMessage(
        EventId = 5404,
        Level = LogLevel.Information,
        Message = "Runner {RunnerId} was refused a lease: distributed review execution is not licensed")]
    private static partial void LogNotLicensed(ILogger logger, Guid runnerId);

    [LoggerMessage(
        EventId = 5405,
        Level = LogLevel.Debug,
        Message = "Review job {JobId} was not offered: client {ClientId} has a publishing pr_wide pass, which runs in process")]
    private static partial void LogSkippedPublishingPrWide(ILogger logger, Guid jobId, Guid clientId);

    // The refusal reaches the runner as an empty answer, because the controller answers it 204 and sends no
    // body. Kept at Debug because every runner in the fleet reaches this on every poll for as long as the
    // ceiling is full; the counter the controller records is what reports the condition to an operator
    // without a line per poll. ProcessingCount is absent when the refusal was made without a count.
    [LoggerMessage(
        EventId = 5406,
        Level = LogLevel.Debug,
        Message = "Runner {RunnerId} was offered no work: the installation is at its ceiling of {Cap} "
                  + "concurrent reviews, with {ProcessingCount} observed as executing")]
    private static partial void LogAtConcurrencyCeiling(
        ILogger logger,
        Guid runnerId,
        int cap,
        int? processingCount);
}
