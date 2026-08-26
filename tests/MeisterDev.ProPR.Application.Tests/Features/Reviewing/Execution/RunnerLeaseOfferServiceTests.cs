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

    // The ceiling has to mean across every host sharing the database, and in a runner fleet that is not the
    // same as "on this host". The same rule the in-process worker applies, and at the same point: before
    // anything is claimed.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    [InlineData(3, 4)]
    public async Task AnInstallationAlreadyAtItsCeiling_OffersNothing(int cap, int processing)
    {
        var runner = this.EnrolledRunner();
        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        executionStore.CountProcessingJobsAsync(Arg.Any<CancellationToken>()).Returns(processing);

        var offer = await this.CreateService(limits: LicensedCeilingOf(cap), executionStore: executionStore)
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.Equal(RunnerLeaseRefusal.ConcurrencyCeilingReached, offer.Refusal);
        Assert.Equal(cap, offer.Ceiling);
        await this._offers.DidNotReceiveWithAnyArgs().GetOfferCandidatesAsync(default, default!, default!, default);
    }

    // The queue behind a full ceiling is not empty, and an operator looking at an idle fleet has to be able
    // to tell the two apart. Both are answered 204, so the refusal is the only thing carrying the
    // difference, and the controller counts it against the ceiling on that basis.
    [Fact]
    public async Task AFullCeiling_IsADifferentAnswerFromAnEmptyQueue()
    {
        var runner = this.EnrolledRunner();
        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        executionStore.CountProcessingJobsAsync(Arg.Any<CancellationToken>()).Returns(2);
        this._offers.GetOfferCandidatesAsync(
                TenantId, Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var atCeiling = await this.CreateService(limits: LicensedCeilingOf(2), executionStore: executionStore)
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));
        var emptyQueue = await this.CreateService()
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.NotEqual(emptyQueue.Refusal, atCeiling.Refusal);
        Assert.Equal(RunnerLeaseRefusal.NoMatchingWork, emptyQueue.Refusal);
        Assert.Null(emptyQueue.Ceiling);
        Assert.Equal(RunnerLeaseRefusal.ConcurrencyCeilingReached, atCeiling.Refusal);
        Assert.Equal(2, atCeiling.Ceiling);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    public async Task AnInstallationBelowItsCeiling_StillLeases(int cap, int processing)
    {
        var runner = this.EnrolledRunner();
        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        executionStore.CountProcessingJobsAsync(Arg.Any<CancellationToken>()).Returns(processing);
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this.GrantClaimFor(job, runner);
        this.PreparationSucceedsFor(job);
        this.ManifestResolves();

        var offer = await this.CreateService(limits: LicensedCeilingOf(cap), executionStore: executionStore)
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
    }

    // The count is read before the claim, so a replica that claims in between makes it stale. The claim
    // itself carries the ceiling, and its refusal ends the offer rather than moving down the candidate list.
    [Fact]
    public async Task AnInstallationWhoseClaimReportsTheCeilingFull_OffersNothing()
    {
        var runner = this.EnrolledRunner();
        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        executionStore.CountProcessingJobsAsync(Arg.Any<CancellationToken>()).Returns(0);
        var first = MakeJob();
        var second = MakeJob();
        this.WithCandidates(runner, first, second);
        this._leases.TryClaimWithinProcessingCapAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ReviewJobCappedClaim.AtCapacity(3, 3));

        var offer = await this.CreateService(limits: LicensedCeilingOf(3), executionStore: executionStore)
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.Equal(RunnerLeaseRefusal.ConcurrencyCeilingReached, offer.Refusal);
        Assert.Equal(3, offer.Ceiling);

        // The numbers the claim measured, not the stale ones read before it.
        Assert.Equal("This installation is running 3 of 3 concurrent reviews. The limit comes from the license in force.", offer.Detail);
        await this._leases.DidNotReceive().TryClaimWithinProcessingCapAsync(
            second.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // A refusal under a licensed ceiling must not state that no license is in force, and one under the
    // community value must not attribute the number to a license. The resolution's source decides which is
    // stated.
    //
    // The community source has three causes, and the two stages here stand for the ones that differ most: no
    // license at all, and a license in force that leaves the limit out. The copy is the same for both,
    // because a licensed installation must not be described as having no license.
    [Theory]
    [InlineData(LicenseStage.None)]
    [InlineData(LicenseStage.Active)]
    public async Task ARefusalUnderTheCommunityCeiling_NamesTheCeilingWithoutCreditingALicense(LicenseStage stage)
    {
        var runner = this.EnrolledRunner();
        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        executionStore.CountProcessingJobsAsync(Arg.Any<CancellationToken>()).Returns(1);

        var offer = await this.CreateService(limits: CommunityCeilingOfOne(stage), executionStore: executionStore)
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.Equal(
            "This installation is running 1 of 1 concurrent reviews. This installation is not entitled to a higher limit.",
            offer.Detail);
    }

    // A refusal made before the admission was reached has no observed count to quote, and still names the
    // ceiling rather than reporting an empty queue.
    [Fact]
    public async Task ARefusalWithNoObservedCount_NamesTheCeilingAlone()
    {
        var runner = this.EnrolledRunner();
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this._leases.TryClaimWithinProcessingCapAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ReviewJobCappedClaim.AtCapacity(2));

        var offer = await this.CreateService(limits: LicensedCeilingOf(2))
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.Equal(
            "This installation is at its limit of 2 concurrent reviews. The limit comes from the license in force.",
            offer.Detail);
    }

    // The uncapped claim cannot carry a ceiling, so a capped installation must not reach for it, and the cap
    // it passes is the resolved number rather than a constant.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public async Task AnInstallationWithACeiling_ClaimsThroughTheCappedClaim(int cap)
    {
        var runner = this.EnrolledRunner();
        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        executionStore.CountProcessingJobsAsync(Arg.Any<CancellationToken>()).Returns(0);
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this.GrantClaimFor(job, runner);
        this.PreparationSucceedsFor(job);
        this.ManifestResolves();

        var offer = await this.CreateService(limits: LicensedCeilingOf(cap), executionStore: executionStore)
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
        await this._leases.Received().TryClaimWithinProcessingCapAsync(job.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), cap, Arg.Any<CancellationToken>());
        await this._leases.DidNotReceiveWithAnyArgs().TryClaimAsync(default, default!, default, default);
    }

    // Under an unlimited ceiling nothing is counted and the claim goes through the uncapped path. Taking the
    // admission would serialize every claim installation-wide while nothing is being counted.
    [Fact]
    public async Task AnInstallationWithAnUnlimitedCeiling_ClaimsUncappedAndCountsNothing()
    {
        var runner = this.EnrolledRunner();
        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this.GrantClaimFor(job, runner);
        this.PreparationSucceedsFor(job);
        this.ManifestResolves();

        var offer = await this.CreateService(limits: UnlimitedCeiling(), executionStore: executionStore)
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
        await executionStore.DidNotReceive().CountProcessingJobsAsync(Arg.Any<CancellationToken>());
        await this._leases.Received().TryClaimAsync(job.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await this._leases.DidNotReceiveWithAnyArgs().TryClaimWithinProcessingCapAsync(default, default!, default, default, default);
    }

    // A host composed without the licensing surface claims uncapped, as the offline and test hosts do. An
    // absent resolver must not produce a ceiling.
    [Fact]
    public async Task AnInstallationWithNoLimitResolver_ClaimsUncapped()
    {
        var runner = this.EnrolledRunner();
        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this.GrantClaimFor(job, runner);
        this.PreparationSucceedsFor(job);
        this.ManifestResolves();

        var offer = await this.CreateService(executionStore: executionStore)
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
        await executionStore.DidNotReceive().CountProcessingJobsAsync(Arg.Any<CancellationToken>());
        await this._leases.DidNotReceiveWithAnyArgs().TryClaimWithinProcessingCapAsync(default, default!, default, default, default);
    }

    // A ceiling of zero admits no review, and the capped claim refuses a cap below one, so the refusal is
    // made without a claim.
    [Fact]
    public async Task AnInstallationWithACeilingOfZero_OffersNothingAndClaimsNothing()
    {
        var runner = this.EnrolledRunner();
        var job = MakeJob();
        this.WithCandidates(runner, job);

        var offer = await this.CreateService(limits: LicensedCeilingOf(0))
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.Equal(RunnerLeaseRefusal.ConcurrencyCeilingReached, offer.Refusal);
        Assert.Equal(0, offer.Ceiling);
        Assert.Contains("0 concurrent reviews", offer.Detail!, StringComparison.Ordinal);
        await this._leases.DidNotReceiveWithAnyArgs().TryClaimWithinProcessingCapAsync(default, default!, default, default, default);
        await this._leases.DidNotReceiveWithAnyArgs().TryClaimAsync(default, default!, default, default);
    }

    // Losing the claim is the normal outcome when several runners ask at once. It must cost the losing runner
    // the candidate rather than the whole offer, or a busy installation would only ever serve the fastest
    // runner.
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

    // The capability is checked on every offer, because a license can lapse while a fleet is enrolled and
    // running. Refused before any queue work, so an installation that lost the capability does not pay for a
    // candidate scan on every poll of every runner.
    [Fact]
    public async Task WithoutTheDistributedExecutionCapability_NoLeaseIsOfferedAndNoQueueWorkHappens()
    {
        var runner = this.EnrolledRunner();

        var offer = await this.CreateService(licensing: CapabilityThatIs(false))
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        // The one refusal the controller maps to 429, with the slot_limit_reached code and this detail as
        // its message.
        Assert.Equal(RunnerLeaseRefusal.NotLicensed, offer.Refusal);
        Assert.Equal("Distributed review execution is not licensed for this installation.", offer.Detail);
        await this._offers.DidNotReceiveWithAnyArgs().GetOfferCandidatesAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task WithTheDistributedExecutionCapability_TheLeaseIsGranted()
    {
        var runner = this.EnrolledRunner();
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this.GrantClaimFor(job, runner);
        this.PreparationSucceedsFor(job);
        this.ManifestResolves();

        var offer = await this.CreateService(licensing: CapabilityThatIs(true))
            .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
    }

    // A host composed without the licensing surface has no service to ask, which reads as available. Reading
    // it as unavailable would refuse every lease on an installation that has no licensing module at all.
    [Fact]
    public async Task WithNoLicensingServiceAtAll_TheLeaseIsGranted()
    {
        var runner = this.EnrolledRunner();
        var job = MakeJob();
        this.WithCandidates(runner, job);
        this.GrantClaimFor(job, runner);
        this.PreparationSucceedsFor(job);
        this.ManifestResolves();

        var offer = await this.CreateService().OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current));

        Assert.True(offer.Granted);
    }

    private static ILicensingCapabilityService CapabilityThatIs(bool enabled)
    {
        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.DistributedExecution, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(enabled));
        return licensing;
    }

    private static ILicenseLimitResolver CreateLimits(LicenseLimitResolution resolution)
    {
        var limits = Substitute.For<ILicenseLimitResolver>();
        limits.ResolveAsync(LicenseLimitKey.ConcurrentReviews, Arg.Any<CancellationToken>()).Returns(resolution);
        return limits;
    }

    private static ILicenseLimitResolver LicensedCeilingOf(long cap)
    {
        return CreateLimits(LicenseLimitResolution.Of(LicenseLimitKey.ConcurrentReviews, cap, LicenseLimitSource.License, LicenseStage.Active));
    }

    private static ILicenseLimitResolver CommunityCeilingOfOne(LicenseStage stage = LicenseStage.None)
    {
        return CreateLimits(LicenseLimitResolution.Of(LicenseLimitKey.ConcurrentReviews, 1, LicenseLimitSource.Community, stage));
    }

    private static ILicenseLimitResolver UnlimitedCeiling()
    {
        return CreateLimits(LicenseLimitResolution.Unlimited(LicenseLimitKey.ConcurrentReviews, LicenseLimitSource.License, LicenseStage.Active));
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
        this._leases.TryClaimWithinProcessingCapAsync(job.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ReviewJobCappedClaim.Granted(lease));
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
    // enforce" and "this replica is not holding the job" the same answer, which is how an unconfigured
    // client's completions were all refused.
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
    // mirror preparation, so every poll cost a generation bump, a full repository clone and a release. The
    // skip has to happen before anything is claimed, and the job stays Pending for the in-process worker.
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
        IBudgetCapsProvider? budgetCaps = null,
        IReviewSpendAccumulator? spend = null,
        ILicenseLimitResolver? limits = null,
        IReviewJobExecutionStore? executionStore = null,
        MeisterDev.ProPR.Application.Interfaces.IClientRegistry? clients = null,
        ILicensingCapabilityService? licensing = null)
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
            budgetCaps,
            spend,
            limits,
            executionStore,
            clients,
            licensing);
    }
}
