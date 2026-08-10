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
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Answers a runner asking for work.
///     <para>
///         The decision stays on the asking side. A runner asks only when it has a free slot, so the control
///         plane never has to maintain a view of who is busy, and a runner that dies takes its own capacity
///         out of the pool by simply not asking again. What the control plane decides is which jobs this
///         particular runner is allowed to see, which is a correctness boundary rather than a preference.
///     </para>
///     <para>
///         Winning a candidate is the same conditional claim the in-process worker uses. Two runners handed
///         the same candidate resolve it in the database, so an offer is never a promise and never has to be
///         held anywhere.
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
    IRunnerSlotEntitlement? slots = null,
    IBudgetCapsProvider? budgetCaps = null,
    IReviewSpendAccumulator? spend = null,
    ILicensingCapabilityService? licensing = null,
    IReviewJobExecutionStore? executionStore = null,
    IClientRegistry? clients = null) : IRunnerLeaseOfferService
{
    /// <inheritdoc />
    public async Task<RunnerLeaseOffer> OfferAsync(RunnerLeaseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // One gate for every call site: the served window is already clamped by the manifest floor, so a
        // version refused here is refused by the heartbeat and the execution surface for the same reason,
        // with the same words. Admitting a below-floor runner used to grant a lease whose manifest it
        // could not deserialize — the generation bumped, the failure unnamed, three such offers failing
        // the job.
        if (!RunnerContractVersion.IsSupported(request.ContractVersion))
        {
            return RunnerLeaseOffer.Refuse(
                RunnerLeaseRefusal.UnsupportedContractVersion,
                RunnerContractVersion.DescribeMismatch(request.ContractVersion));
        }

        // A runner that asks with no free slot is a runner whose own accounting has drifted. Refusing is
        // cheaper than trusting it, and the typed reason makes the drift visible instead of looking like an
        // empty queue.
        if (request.FreeSlots <= 0)
        {
            return RunnerLeaseOffer.Refuse(RunnerLeaseRefusal.NoFreeCapacity);
        }

        var runner = await runners.FindByIdAsync(request.RunnerId, ct);
        if (runner is null || runner.State != RunnerState.Enrolled)
        {
            return RunnerLeaseOffer.Refuse(RunnerLeaseRefusal.RegistrationNotUsable);
        }

        if (slots is not null)
        {
            var admission = await slots.AdmitAsync(runner.Id, ct);
            if (admission.Refusal != RunnerLeaseRefusal.None)
            {
                LogSlotRefusal(logger, runner.Id, admission.Refusal.ToString());
                return RunnerLeaseOffer.Refuse(admission.Refusal, admission.Detail);
            }
        }

        // The same one-review-at-a-time rule the in-process worker applies, at the same kind of moment:
        // before anything is claimed. Without the parallel-execution capability, one review runs across
        // the whole installation — and a runner fleet is exactly the topology where "across every host
        // sharing the database" and "on this host" stop being the same thing, so the database's count
        // decides, not anybody's local view.
        if (licensing is not null
            && executionStore is not null
            && !await licensing.IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution, ct)
            && await executionStore.CountProcessingJobsAsync(ct) > 0)
        {
            return RunnerLeaseOffer.Refuse(
                RunnerLeaseRefusal.NoMatchingWork,
                "A review is already running and parallel review execution is not licensed.");
        }

        var candidates = await offers.GetOfferCandidatesAsync(
            runner.TenantId,
            runner.ClientScope,
            runner.Tags,
            leaseOptions.Value.ClaimCandidateLimit,
            ct);

        var owner = runner.Id.ToString("D");
        var prWideByClient = new Dictionary<Guid, bool>();
        foreach (var job in candidates)
        {
            // A publishing pr_wide pass never dispatches, and the manifest resolver's refusal comes after
            // the claim and the mirror preparation. Skipping before the claim is what keeps that refusal
            // from becoming a hot loop — generation bump, full repository preparation, release, every
            // poll, forever — while the job itself stays claimable by the in-process worker, which runs it.
            if (await this.HasPublishingPrWidePassAsync(job.ClientId, prWideByClient, ct))
            {
                LogSkippedPublishingPrWide(logger, job.Id, job.ClientId);
                continue;
            }

            var lease = await leases.TryClaimAsync(job.Id, owner, leaseOptions.Value.LeaseDuration, ct);
            if (lease is null)
            {
                // Somebody else won it between the read and the claim. That is the normal outcome under
                // load, not an error, so the next candidate is simply tried.
                continue;
            }

            // Held until ownership has actually been handed to the runner. Everything between the claim
            // and the grant can throw, and the request token is the runner's: a runner that disconnects
            // mid-preparation would otherwise leave the job Processing until its lease expired.
            var granted = false;
            try
            {
                var preparation = await preparer.PrepareAsync(job, lease, ct);
                if (!preparation.Succeeded)
                {
                    // The job cannot be dispatched, which is nothing to do with this runner. Handing the
                    // lease back means the failure does not also cost the runner its turn.
                    LogDispatchPreparationFailed(logger, job.Id, preparation.Failure ?? "unknown");
                    continue;
                }

                var resolution = await manifests.ResolveAsync(preparation.Request!, ct);
                if (!resolution.Succeeded)
                {
                    LogManifestResolutionFailed(logger, job.Id, resolution.Refusal ?? "unknown");
                    continue;
                }

                // Registered before the manifest is handed over, never after. The relay charges every
                // completion against this scope and refuses when it cannot find one, so a runner that
                // received its manifest first could make a call the control plane would have to turn away.
                budgets.Register(job.Id, await this.ResolveBudgetScopeAsync(job, ct));

                LogLeaseGranted(logger, job.Id, runner.Id, lease.Generation);
                granted = true;
                return RunnerLeaseOffer.Grant(resolution.Manifest!);
            }
            finally
            {
                if (!granted)
                {
                    // Released on a token of its own. Using the request's would mean an aborted request
                    // cancels the very cleanup the abort made necessary. The workspace goes too: an offer
                    // that stopped after preparation would otherwise leave two checkouts on disk per
                    // refusal, and nothing later ever takes them back.
                    budgets.Release(job.Id);
                    tools.Release(job.Id);
                    await workspaces.ReleaseAsync(job.Id);
                    await leases.TryReleaseAsync(lease, CancellationToken.None);
                }
            }
        }

        return RunnerLeaseOffer.Refuse(RunnerLeaseRefusal.NoMatchingWork);
    }

    /// <summary>
    ///     The budget this job's relayed completions are charged against.
    ///     <para>
    ///         Always a scope, even for a client that configures no caps. The registry is what tells the
    ///         relay this replica is holding the job open, and returning null for an unconfigured client
    ///         would make "nothing to enforce" and "not my job" the same answer — which is how every
    ///         completion for such a client ends up refused.
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

    [LoggerMessage(EventId = 5404, Level = LogLevel.Information, Message = "Runner {RunnerId} was refused a lease: {Refusal}")]
    private static partial void LogSlotRefusal(ILogger logger, Guid runnerId, string refusal);

    [LoggerMessage(
        EventId = 5405,
        Level = LogLevel.Debug,
        Message = "Review job {JobId} was not offered: client {ClientId} has a publishing pr_wide pass, which runs in process")]
    private static partial void LogSkippedPublishingPrWide(ILogger logger, Guid jobId, Guid clientId);
}
