// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     The rules around the offer, separate from the SQL that selects candidates. What matters here is that
///     a job which cannot be dispatched never costs a runner its turn, and that every refusal an operator
///     may have to explain arrives with its own name rather than as an empty answer.
/// </summary>
public sealed class RunnerLeaseOfferServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IRunnerRegistry _runners = Substitute.For<IRunnerRegistry>();
    private readonly IRunnerLeaseOfferStore _offers = Substitute.For<IRunnerLeaseOfferStore>();
    private readonly IReviewJobLeaseStore _leases = Substitute.For<IReviewJobLeaseStore>();
    private readonly IRunnerJobDispatchPreparer _preparer = Substitute.For<IRunnerJobDispatchPreparer>();
    private readonly IRunnerJobManifestResolver _manifests = Substitute.For<IRunnerJobManifestResolver>();

    [Fact]
    public async Task ARunnerSpeakingAnUnsupportedContract_IsRefusedByName()
    {
        var offer = await this.CreateService().OfferAsync(new RunnerLeaseRequest(Guid.NewGuid(), 1, RunnerContractVersion.Current + 5));

        Assert.Equal(RunnerLeaseRefusal.UnsupportedContractVersion, offer.Refusal);
        Assert.NotNull(offer.Detail);
    }

    // Inside the window but below the manifest floor. Admitting this runner used to grant a lease whose
    // manifest it could not deserialize: generation bumped, an unnamed JsonException in its loop, and
    // three such offers failing the job. This is the clean refusal version of that outcome.
    [Fact]
    public async Task ARunnerBelowTheManifestFloor_IsRefusedNamingTheFloor()
    {
        var offer = await this.CreateService().OfferAsync(new RunnerLeaseRequest(Guid.NewGuid(), 1, RunnerContractVersion.OldestManifestCompatible - 1));

        Assert.Equal(RunnerLeaseRefusal.UnsupportedContractVersion, offer.Refusal);
        Assert.Contains("manifest", offer.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    // A runner that asks with no room has lost track of its own capacity. Saying so beats handing it work
    // it cannot run, and beats answering as though the queue were empty.
    [Fact]
    public async Task ARunnerWithNoFreeSlot_IsRefusedWithoutTouchingTheQueue()
    {
        var offer = await this.CreateService().OfferAsync(new RunnerLeaseRequest(Guid.NewGuid(), 0, RunnerContractVersion.Current));

        Assert.Equal(RunnerLeaseRefusal.NoFreeCapacity, offer.Refusal);
        await this._offers.DidNotReceiveWithAnyArgs().GetOfferCandidatesAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ARevokedRunner_IsRefusedRatherThanOfferedWork()
    {
        var runner = MakeRunner();
        runner.Revoke(DateTimeOffset.UtcNow);
        this._runners.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);

        var offer = await this.CreateService().OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.Equal(RunnerLeaseRefusal.RegistrationNotUsable, offer.Refusal);
        await this._offers.DidNotReceiveWithAnyArgs().GetOfferCandidatesAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task AnEmptyQueue_IsAnAnswerRatherThanAnError()
    {
        var runner = this.EnrolledRunner();
        this._offers.GetOfferCandidatesAsync(
                TenantId, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var offer = await this.CreateService().OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.Equal(RunnerLeaseRefusal.NoMatchingWork, offer.Refusal);
    }

    // One review at a time has to mean across every host sharing the database — and a runner fleet is
    // exactly the topology where that stops being the same as "on this host". The same rule the in-process
    // worker applies, at the same kind of moment: before anything is claimed.
    [Fact]
    public async Task AnUnlicensedInstallation_WithAReviewAlreadyRunning_OffersNothing()
    {
        var runner = this.EnrolledRunner();
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution, Arg.Any<CancellationToken>()).Returns(false);
        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        executionStore.CountProcessingJobsAsync(Arg.Any<CancellationToken>()).Returns(1);

        var offer = await this.CreateService(licensing: licensing, executionStore: executionStore)
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.Equal(RunnerLeaseRefusal.NoMatchingWork, offer.Refusal);
        await this._offers.DidNotReceiveWithAnyArgs().GetOfferCandidatesAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task AnUnlicensedInstallation_WithNothingRunning_StillLeases()
    {
        var runner = this.EnrolledRunner();
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution, Arg.Any<CancellationToken>()).Returns(false);
        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        executionStore.CountProcessingJobsAsync(Arg.Any<CancellationToken>()).Returns(0);
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this.GrantClaimFor(job, runner);
        this.PreparationSucceedsFor(job);
        this.ManifestResolves();

        var offer = await this.CreateService(licensing: licensing, executionStore: executionStore)
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
    }

    // A licensed installation never pays the count query: the clamp exists for the unlicensed case only.
    [Fact]
    public async Task ALicensedInstallation_NeverCountsProcessingJobs()
    {
        var runner = this.EnrolledRunner();
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution, Arg.Any<CancellationToken>()).Returns(true);
        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this.GrantClaimFor(job, runner);
        this.PreparationSucceedsFor(job);
        this.ManifestResolves();

        var offer = await this.CreateService(licensing: licensing, executionStore: executionStore)
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
        await executionStore.DidNotReceive().CountProcessingJobsAsync(Arg.Any<CancellationToken>());
    }

    // Losing the claim is the normal outcome when several runners ask at once. It must cost the loser the
    // candidate, not the whole offer, or a busy installation would starve every runner but the fastest.
    [Fact]
    public async Task LosingAClaim_MovesToTheNextCandidateRatherThanGivingUp()
    {
        var runner = this.EnrolledRunner();
        var lost = MakeJob();
        var won = MakeJob();
        this.WithCandidates(runner, lost, won);
        this._leases.TryClaimAsync(lost.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns((ReviewJobLease?)null);
        this.GrantClaimFor(won, runner);
        this.PreparationSucceedsFor(won);
        this.ManifestResolves();

        var offer = await this.CreateService().OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
    }

    // A job that cannot be prepared is a property of the job, not of the runner asking. Keeping the lease
    // would strand the job on a runner that never got a manifest, and would cost the runner its turn.
    [Fact]
    public async Task AJobThatCannotBePrepared_IsReturnedToTheQueueAndTheNextIsTried()
    {
        var runner = this.EnrolledRunner();
        var broken = MakeJob();
        var fine = MakeJob();
        this.WithCandidates(runner, broken, fine);
        var brokenLease = this.GrantClaimFor(broken, runner);
        this.GrantClaimFor(fine, runner);
        this._preparer.PrepareAsync(broken, Arg.Any<ReviewJobLease>(), Arg.Any<CancellationToken>())
            .Returns(RunnerJobDispatchPreparation.Failed("no workspace"));
        this.PreparationSucceedsFor(fine);
        this.ManifestResolves();

        var offer = await this.CreateService().OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
        await this._leases.Received(1).TryReleaseAsync(brokenLease, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AJobWhoseManifestCannotBeResolved_IsAlsoReturnedToTheQueue()
    {
        var runner = this.EnrolledRunner();
        var job = MakeJob();
        this.WithCandidates(runner, job);
        var lease = this.GrantClaimFor(job, runner);
        this.PreparationSucceedsFor(job);
        this._manifests.ResolveAsync(Arg.Any<RunnerJobManifestRequest>(), Arg.Any<CancellationToken>())
            .Returns(RunnerJobManifestResolution.Refused("no model bound"));

        var offer = await this.CreateService().OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.Equal(RunnerLeaseRefusal.NoMatchingWork, offer.Refusal);
        await this._leases.Received(1).TryReleaseAsync(lease, Arg.Any<CancellationToken>());
    }

    // The entitlement is consulted before any queue work, so an installation that is out of slots does not
    // pay for a candidate scan on every poll of every runner.
    [Fact]
    public async Task WhenTheEntitlementRefuses_NoQueueWorkHappensAtAll()
    {
        var runner = this.EnrolledRunner();
        var slots = Substitute.For<IRunnerSlotEntitlement>();
        slots.AdmitAsync(runner.Id, Arg.Any<CancellationToken>())
            .Returns(new RunnerSlotAdmission(RunnerLeaseRefusal.SlotLimitReached, "3 of 3 slots are held."));

        var offer = await this.CreateService(slots).OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.Equal(RunnerLeaseRefusal.SlotLimitReached, offer.Refusal);
        Assert.Equal("3 of 3 slots are held.", offer.Detail);
        await this._offers.DidNotReceiveWithAnyArgs().GetOfferCandidatesAsync(default, default!, default!, default);
    }

    private static ReviewRunner MakeRunner()
    {
        return new ReviewRunner(
            Guid.NewGuid(),
            TenantId,
            "runner-01",
            [],
            RunnerContractVersion.Current,
            "hashed:secret",
            "LOOKUP",
            DateTimeOffset.UtcNow.AddDays(30),
            DateTimeOffset.UtcNow);
    }

    private static ReviewJob MakeJob()
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
    }

    private ReviewRunner EnrolledRunner()
    {
        var runner = MakeRunner();
        this._runners.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);
        return runner;
    }

    private void WithCandidates(ReviewRunner runner, params ReviewJob[] jobs)
    {
        this._offers.GetOfferCandidatesAsync(
                runner.TenantId, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(jobs);
    }

    private ReviewJobLease GrantClaimFor(ReviewJob job, ReviewRunner runner)
    {
        var lease = new ReviewJobLease(job.Id, runner.Id.ToString("D"), 1, DateTimeOffset.UtcNow.AddMinutes(2));
        this._leases.TryClaimAsync(job.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(lease);
        return lease;
    }

    private void PreparationSucceedsFor(ReviewJob job)
    {
        this._preparer.PrepareAsync(job, Arg.Any<ReviewJobLease>(), Arg.Any<CancellationToken>())
            .Returns(ci => RunnerJobDispatchPreparation.Ready(new RunnerJobManifestRequest(job, ci.Arg<ReviewJobLease>(), "main", [], "path", 1024)));
    }

    private void ManifestResolves()
    {
        this._manifests.ResolveAsync(Arg.Any<RunnerJobManifestRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => RunnerJobManifestResolution.Resolved(MakeManifest(ci.Arg<RunnerJobManifestRequest>())));
    }

    private static RunnerJobManifest MakeManifest(RunnerJobManifestRequest request)
    {
        return new RunnerJobManifest(
            RunnerContractVersion.Current,
            request.Job.Id,
            request.Job.ClientId,
            request.Lease.Generation,
            new RunnerReviewTarget(
                "azuredevops", "https://dev.azure.com/x", "project", "repo", "repo", "1", 1, 1,
                "title", null, "feature", "main", "head", "base", [], []),
            new RunnerWorkspaceReference(request.WorkspaceFetchPath, "head", "base", request.MaxWorkspaceTransferBytes),
            new RunnerModelBinding("reviewer", "model", "OpenAi", "None", null, null, null, false, true, true),
            [],
            new RunnerPromptConfiguration(null, null, new Dictionary<string, string>()),
            [],
            [],
            null,
            new RunnerTraceContext(string.Empty, null));
    }


    // The defect that stopped every remote review: nothing registered a budget, so the relay's lookup
    // always missed and refused every completion. A granted job must be held open before its manifest
    // leaves, because the runner can call the relay the moment it has one.
    [Fact]
    public async Task AGrantedJob_IsHeldOpenBeforeItsManifestIsHandedOver()
    {
        var runner = this.EnrolledRunner();
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this.GrantClaimFor(job, runner);
        this.PreparationSucceedsFor(job);
        this.ManifestResolves();

        var offer = await this.CreateService().OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
        Assert.NotNull(this._budgets.Find(job.Id));
    }

    // A client that configures no caps still gets a scope. Returning null for it would make "nothing to
    // enforce" and "this replica is not holding the job" the same answer, which is exactly how an
    // unconfigured client's completions all ended up refused.
    [Fact]
    public async Task AClientWithNoCapsConfigured_IsStillHeldOpen()
    {
        var caps = Substitute.For<IBudgetCapsProvider>();
        caps.GetCapsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(BudgetCaps.None);
        var spend = Substitute.For<IReviewSpendAccumulator>();
        var runner = this.EnrolledRunner();
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this.GrantClaimFor(job, runner);
        this.PreparationSucceedsFor(job);
        this.ManifestResolves();

        var offer = await this.CreateService(budgetCaps: caps, spend: spend)
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
        var scope = this._budgets.Find(job.Id);
        Assert.NotNull(scope);
        Assert.False(scope!.Caps.AnyConfigured);

        // No baseline is read when nothing can be exceeded; that read is a query per lease.
        await spend.DidNotReceive().GetBaselineAsync(Arg.Any<ReviewSpendSubject>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>());
    }

    // An offer that fails after the claim hands the lease back, and must hand the scope back with it.
    [Fact]
    public async Task AnOfferThatFailsAfterClaiming_LeavesNothingHeldOpen()
    {
        var runner = this.EnrolledRunner();
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this.GrantClaimFor(job, runner);
        this._preparer.PrepareAsync(job, Arg.Any<ReviewJobLease>(), Arg.Any<CancellationToken>())
            .Returns(RunnerJobDispatchPreparation.Failed("no workspace"));

        var offer = await this.CreateService().OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.False(offer.Granted);
        Assert.Null(this._budgets.Find(job.Id));
    }

    // A publishing pr_wide pass never dispatches, and the refusal used to come after the claim and the
    // mirror preparation: generation bump, full repository clone, release — every poll, forever. The skip
    // has to happen before anything is claimed, and the job stays Pending for the in-process worker.
    [Fact]
    public async Task AClientWithAPublishingPrWidePass_IsSkippedBeforeAnythingIsClaimed()
    {
        var runner = this.EnrolledRunner();
        var job = MakeJob();
        this.WithCandidates(runner, job);
        var clients = Substitute.For<MeisterDev.ProPR.Application.Interfaces.IClientRegistry>();
        clients.GetReviewPassesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns([new MeisterDev.ProPR.Application.ValueObjects.ReviewPassSpec(Guid.NewGuid(), Scope: ReviewPassScope.PrWide, Shadow: false)]);

        var offer = await this.CreateService(clients: clients).OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.Equal(RunnerLeaseRefusal.NoMatchingWork, offer.Refusal);
        await this._leases.DidNotReceiveWithAnyArgs().TryClaimAsync(default, default!, default, default);
        await this._preparer.DidNotReceiveWithAnyArgs().PrepareAsync(default!, default!, default);
    }

    // A shadow pr_wide entry publishes nothing, so it still dispatches: skipping it would change
    // telemetry, not the review.
    [Fact]
    public async Task AClientWhosePrWidePassIsShadow_IsStillOffered()
    {
        var runner = this.EnrolledRunner();
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this.GrantClaimFor(job, runner);
        this.PreparationSucceedsFor(job);
        this.ManifestResolves();
        var clients = Substitute.For<MeisterDev.ProPR.Application.Interfaces.IClientRegistry>();
        clients.GetReviewPassesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns([new MeisterDev.ProPR.Application.ValueObjects.ReviewPassSpec(Guid.NewGuid(), Scope: ReviewPassScope.PrWide, Shadow: true)]);

        var offer = await this.CreateService(clients: clients).OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
    }

    private readonly RunnerJobBudgetRegistry _budgets = new();
    private readonly RunnerJobToolsRegistry _tools = new();
    private readonly RunnerWorkspaceRegistry _workspaces = new();

    private RunnerLeaseOfferService CreateService(
        IRunnerSlotEntitlement? slots = null,
        IBudgetCapsProvider? budgetCaps = null,
        IReviewSpendAccumulator? spend = null,
        ILicensingCapabilityService? licensing = null,
        IReviewJobExecutionStore? executionStore = null,
        MeisterDev.ProPR.Application.Interfaces.IClientRegistry? clients = null)
    {
        return new RunnerLeaseOfferService(
            this._runners,
            this._offers,
            this._leases,
            this._preparer,
            this._manifests,
            Microsoft.Extensions.Options.Options.Create(new ReviewLeaseOptions()),
            this._budgets,
            this._tools,
            this._workspaces,
            NullLogger<RunnerLeaseOfferService>.Instance,
            slots,
            budgetCaps,
            spend,
            licensing,
            executionStore,
            clients);
    }
}
