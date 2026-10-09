// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewByCoordinates;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Intake.Commands;

public sealed class SubmitReviewByCoordinatesHandlerTests
{
    [Theory]
    [InlineData("crawl")]
    [InlineData("webhook")]
    [InlineData("management")]
    public async Task HandleAsync_CustomerInitialCoverageReadFailureReturnsNamedFailureBeforeProviderOrAdmission(string read)
    {
        var repository = SubstituteCrawlRepository(GitHubConfiguration());
        var webhooks = SubstituteWebhookRepository();
        FailCoverageRead(repository, webhooks, read, new InvalidOperationException("initial coverage read detail"));
        var query = SubstituteQueryService(OpenPullRequest());
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(crawlConfigurations: repository, webhookConfigurations: webhooks, queryService: query, synchronization: synchronization);

        var result = await sut.HandleAsync(Command() with { IsCustomerRequest = true });

        Assert.Equal(SubmitReviewByCoordinatesOutcome.SubmissionFailed, result.Outcome);
        Assert.Null(result.JobId);
        Assert.Equal("The review could not be submitted. Try again; contact the operator if the failure continues.", result.Reason);
        await repository.DidNotReceiveWithAnyArgs().AcquireReviewTargetAdmissionAsync(default);
        await query.DidNotReceiveWithAnyArgs().GetReviewAsync(default, default!);
        await synchronization.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    [Theory]
    [InlineData("crawl")]
    [InlineData("webhook")]
    [InlineData("management")]
    public async Task HandleAsync_CustomerInitialCoverageReadCancellationPropagatesBeforeProviderOrAdmission(string read)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var repository = SubstituteCrawlRepository(GitHubConfiguration());
        var webhooks = SubstituteWebhookRepository();
        FailCoverageRead(repository, webhooks, read, new OperationCanceledException(cancellation.Token));
        var query = SubstituteQueryService(OpenPullRequest());
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(crawlConfigurations: repository, webhookConfigurations: webhooks, queryService: query, synchronization: synchronization);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => sut.HandleAsync(
            Command() with { IsCustomerRequest = true }, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        await repository.DidNotReceiveWithAnyArgs().AcquireReviewTargetAdmissionAsync(default);
        await query.DidNotReceiveWithAnyArgs().GetReviewAsync(default, default!);
        await synchronization.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    [Theory]
    [InlineData("crawl")]
    [InlineData("webhook")]
    public async Task HandleAsync_PrivilegedInitialCoverageReadFailurePreservesException(string read)
    {
        var repository = SubstituteCrawlRepository(GitHubConfiguration());
        var webhooks = SubstituteWebhookRepository();
        var failure = new InvalidOperationException("privileged coverage read detail");
        FailCoverageRead(repository, webhooks, read, failure);
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(crawlConfigurations: repository, webhookConfigurations: webhooks, synchronization: synchronization);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => sut.HandleAsync(Command())));
        await repository.DidNotReceiveWithAnyArgs().AcquireReviewTargetAdmissionAsync(default);
        await synchronization.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    [Fact]
    public async Task HandleAsync_CustomerAdmissionTimeoutReturnsNamedSubmissionFailure()
    {
        var repository = SubstituteCrawlRepository(GitHubConfiguration());
        repository.AcquireReviewTargetAdmissionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncDisposable>(_ => throw new TimeoutException("admission acquisition detail"));
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(crawlConfigurations: repository, synchronization: synchronization);

        var result = await sut.HandleAsync(Command() with { IsCustomerRequest = true });

        Assert.Equal(SubmitReviewByCoordinatesOutcome.SubmissionFailed, result.Outcome);
        Assert.Null(result.JobId);
        Assert.Equal("The review could not be submitted. Try again; contact the operator if the failure continues.", result.Reason);
        Assert.DoesNotContain("admission acquisition detail", result.Reason!, StringComparison.Ordinal);
        await synchronization.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    [Fact]
    public async Task HandleAsync_CustomerProtectedReadFailureReturnsNamedFailureAndDisposesLease()
    {
        var configuration = GitHubConfiguration();
        var repository = SubstituteCrawlRepository(configuration);
        var lease = Substitute.For<IAsyncDisposable>();
        repository.AcquireReviewTargetAdmissionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(lease);
        var reads = 0;
        repository.GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<CrawlConfigurationDto>>(_ => ++reads == 1 ? [configuration] : throw new InvalidOperationException("protected read detail"));
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(crawlConfigurations: repository, synchronization: synchronization);

        var result = await sut.HandleAsync(Command() with { IsCustomerRequest = true });

        Assert.Equal(SubmitReviewByCoordinatesOutcome.SubmissionFailed, result.Outcome);
        Assert.Equal("The review could not be submitted. Try again; contact the operator if the failure continues.", result.Reason);
        Assert.DoesNotContain("protected read detail", result.Reason!, StringComparison.Ordinal);
        await lease.Received(1).DisposeAsync();
        await synchronization.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_CustomerAdmissionCancellationPropagatesAndDisposesAcquiredLease(bool cancelFreshRead)
    {
        using var cancellation = new CancellationTokenSource();
        var configuration = GitHubConfiguration();
        var repository = SubstituteCrawlRepository(configuration);
        var lease = Substitute.For<IAsyncDisposable>();
        repository.AcquireReviewTargetAdmissionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncDisposable>(_ => cancelFreshRead ? lease : throw new OperationCanceledException(cancellation.Token));
        var reads = 0;
        repository.GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<CrawlConfigurationDto>>(_ =>
                ++reads == 1 || !cancelFreshRead ? [configuration] : throw new OperationCanceledException(cancellation.Token));
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(crawlConfigurations: repository, synchronization: synchronization);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => sut.HandleAsync(
            Command() with { IsCustomerRequest = true }, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        if (cancelFreshRead)
        {
            await lease.Received(1).DisposeAsync();
        }
        else
        {
            await lease.DidNotReceive().DisposeAsync();
        }

        await synchronization.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task HandleAsync_CustomerAdmissionLeaseRemainsHeldThroughQueueAcceptance(string queueResult)
    {
        var repository = SubstituteCrawlRepository(GitHubConfiguration());
        var lease = Substitute.For<IAsyncDisposable>();
        var disposed = false;
        lease.DisposeAsync().Returns(_ =>
        {
            disposed = true;
            return ValueTask.CompletedTask;
        });
        repository.AcquireReviewTargetAdmissionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(lease);
        var synchronization = Substitute.For<IPullRequestSynchronizationService>();
        synchronization.SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>())
            .Returns<PullRequestSynchronizationOutcome>(_ =>
            {
                Assert.False(disposed);
                return queueResult switch
                {
                    "failed" => throw new InvalidOperationException("queue store detail"),
                    "cancelled" => throw new OperationCanceledException(),
                    _ => Submitted(),
                };
            });
        var sut = Handler(crawlConfigurations: repository, synchronization: synchronization);

        if (queueResult == "cancelled")
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => sut.HandleAsync(Command() with { IsCustomerRequest = true }));
        }
        else
        {
            Assert.Equal(
                queueResult == "submitted" ? SubmitReviewByCoordinatesOutcome.Submitted : SubmitReviewByCoordinatesOutcome.SubmissionFailed,
                (await sut.HandleAsync(Command() with { IsCustomerRequest = true })).Outcome);
        }

        Assert.True(disposed);
        await lease.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task HandleAsync_PrivilegedSubmissionDoesNotAcquireCustomerAdmission()
    {
        var repository = SubstituteCrawlRepository(GitHubConfiguration());
        repository.AcquireReviewTargetAdmissionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<IAsyncDisposable>(_ => throw new TimeoutException());
        var sut = Handler(crawlConfigurations: repository);

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, (await sut.HandleAsync(Command())).Outcome);
        await repository.DidNotReceiveWithAnyArgs().AcquireReviewTargetAdmissionAsync(default);
    }

    [Theory]
    [InlineData(ReviewTargetLifecycle.Disabled, "12345")]
    [InlineData(ReviewTargetLifecycle.Removed, "acme/propr")]
    public async Task HandleAsync_CustomerExcludedTargetCannotUseBroadCoverage(ReviewTargetLifecycle lifecycle, string requestedId)
    {
        var target = GitHubConfiguration() with { ReviewTargetLifecycle = lifecycle };
        var broad = target with { Id = Guid.NewGuid(), RepoFilters = [], ReviewTargetLifecycle = ReviewTargetLifecycle.Enabled };
        var query = SubstituteQueryService(OpenPullRequest());
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(crawlConfigurations: SubstituteCrawlRepository(broad, target), queryService: query, synchronization: synchronization);

        Assert.Equal(
            SubmitReviewByCoordinatesOutcome.NotAuthorized,
            (await sut.HandleAsync(Command() with { RepositoryId = requestedId, IsCustomerRequest = true })).Outcome);
        await query.DidNotReceiveWithAnyArgs().GetReviewAsync(default, default!);
        await synchronization.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_FreshUnrelatedRepositoryCannotUseRequestedCanonicalAuthorization(bool hasBroadFallback)
    {
        var canonical = GitHubConfiguration();
        var unrelated = new RepositoryRef(GitHubHost(), "67890", "acme", "acme/other");
        var item = OpenPullRequest() with
        {
            Repository = unrelated,
            CodeReview = new CodeReviewRef(unrelated, CodeReviewPlatformKind.PullRequest, "7", 7), ReviewRevision = null
        };
        var query = SubstituteQueryService(item, Revision());
        var synchronization = SubstituteSynchronization(Submitted());
        var broad = canonical with { Id = Guid.NewGuid(), RepoFilters = [] };
        var sut = Handler(
            crawlConfigurations: SubstituteCrawlRepository(hasBroadFallback ? [broad, canonical] : [canonical]),
            queryService: query, synchronization: synchronization);

        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotAuthorized, (await sut.HandleAsync(Command())).Outcome);
        await query.DidNotReceiveWithAnyArgs().GetLatestRevisionAsync(default, default!);
        await synchronization.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    [Theory]
    [InlineData("feature/unselected", SubmitReviewByCoordinatesOutcome.NotSubmittable)]
    [InlineData("main", SubmitReviewByCoordinatesOutcome.Submitted)]
    public async Task HandleAsync_ProviderCorrectedNativeRepositoryUsesCanonicalPolicyAndSnapshot(string branch, SubmitReviewByCoordinatesOutcome expected)
    {
        var canonical = GitHubConfiguration();
        canonical = canonical with
        {
            IsActive = false, ReviewTemperature = 0.25f,
            RepoFilters = [canonical.RepoFilters[0] with { TargetBranchPatterns = ["main"] }],
        };
        var broad = GitHubConfiguration() with { Id = Guid.NewGuid(), RepoFilters = [], ReviewTemperature = 0.75f };
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(
            crawlConfigurations: SubstituteCrawlRepository(broad, canonical),
            queryService: SubstituteQueryService(OpenPullRequest() with { TargetBranch = branch }), synchronization: synchronization);

        Assert.Equal(expected, (await sut.HandleAsync(Command() with { RepositoryId = "acme/propr" })).Outcome);
        if (expected == SubmitReviewByCoordinatesOutcome.Submitted)
        {
            await synchronization.Received(1).SynchronizeAsync(
                Arg.Is<PullRequestSynchronizationRequest>(request => request.RepositoryId == "12345" && request.ReviewTemperature == 0.25f),
                Arg.Any<CancellationToken>());
        }
        else
        {
            await synchronization.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
        }
    }

    [Theory]
    [InlineData("main", SubmitReviewByCoordinatesOutcome.Submitted)]
    [InlineData("feature/other", SubmitReviewByCoordinatesOutcome.NotSubmittable)]
    public async Task HandleAsync_GitLabPathToNativeCorrectionRetainsCanonicalPolicy(string branch, SubmitReviewByCoordinatesOutcome expected)
    {
        var canonical = GitHubConfiguration() with { Provider = ScmProvider.GitLab, ReviewTemperature = 0.25f };
        canonical = canonical with
        {
            RepoFilters =
            [
                canonical.RepoFilters[0] with
                {
                    CanonicalSourceRef = new CanonicalSourceReferenceDto("gitLab", "12345"), TargetBranchPatterns = ["main"]
                }
            ]
        };
        var broad = canonical with { Id = Guid.NewGuid(), RepoFilters = [], ReviewTemperature = 0.75f };
        var repository = new RepositoryRef(new ProviderHostRef(ScmProvider.GitLab, canonical.ProviderScopePath), "12345", "acme", "acme/propr");
        var item = OpenPullRequest() with
        {
            Provider = ScmProvider.GitLab, Repository = repository, TargetBranch = branch,
            CodeReview = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "7", 7)
        };
        var query = SubstituteQueryService(item);
        query.Provider.Returns(ScmProvider.GitLab);
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(crawlConfigurations: SubstituteCrawlRepository(broad, canonical), queryService: query, synchronization: synchronization);
        Assert.Equal(expected, (await sut.HandleAsync(Command() with { RepositoryId = "acme/propr" })).Outcome);
        if (expected == SubmitReviewByCoordinatesOutcome.Submitted)
        {
            await synchronization.Received(1).SynchronizeAsync(
                Arg.Is<PullRequestSynchronizationRequest>(request =>
                    request.RepositoryId == "12345" && request.ReviewTemperature == 0.25f), Arg.Any<CancellationToken>());
        }
        else
        {
            await synchronization.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
        }
    }

    [Fact]
    public async Task HandleAsync_WithoutCanonicalTargetPreservesLegacyMultiRepositoryFallback()
    {
        var configuration = GitHubConfiguration();
        configuration = configuration with
        {
            RepoFilters =
            [
                configuration.RepoFilters[0] with { TargetBranchPatterns = ["release/*"] },
                configuration.RepoFilters[0] with
                {
                    Id = Guid.NewGuid(), CanonicalSourceRef = new CanonicalSourceReferenceDto("gitHub", "other"), RepositoryName = "other"
                },
            ],
        };
        var inactive = configuration with { Id = Guid.NewGuid(), IsActive = false, ReviewTemperature = 0.25f };
        var active = configuration with { ReviewTemperature = 0.75f };
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(crawlConfigurations: SubstituteCrawlRepository(inactive, active), synchronization: synchronization);

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, (await sut.HandleAsync(Command())).Outcome);
        await synchronization.Received(1).SynchronizeAsync(
            Arg.Is<PullRequestSynchronizationRequest>(request => request.ReviewTemperature == 0.75f), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CanonicalAllPolicyOverridesOverlappingRestrictedMultiRepositoryConfiguration()
    {
        var canonical = GitHubConfiguration() with { IsActive = false, ReviewTemperature = 0.25f };
        var generic = GitHubConfiguration() with
        {
            Id = Guid.NewGuid(), ReviewTemperature = 0.75f,
            RepoFilters =
            [
                canonical.RepoFilters[0] with { TargetBranchPatterns = ["release/*"] },
                canonical.RepoFilters[0] with
                {
                    Id = Guid.NewGuid(), CanonicalSourceRef = new CanonicalSourceReferenceDto("gitHub", "other"), RepositoryName = "other"
                },
            ],
        };
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(crawlConfigurations: SubstituteCrawlRepository(generic, canonical), synchronization: synchronization);

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, (await sut.HandleAsync(Command())).Outcome);
        await synchronization.Received(1).SynchronizeAsync(
            Arg.Is<PullRequestSynchronizationRequest>(request => request.ReviewTemperature == 0.25f), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_CanonicalRestrictedPolicyOverridesOverlappingAllMultiRepositoryConfiguration()
    {
        var canonical = GitHubConfiguration();
        var generic = canonical with
        {
            Id = Guid.NewGuid(),
            RepoFilters =
            [
                canonical.RepoFilters[0],
                canonical.RepoFilters[0] with
                {
                    Id = Guid.NewGuid(), CanonicalSourceRef = new CanonicalSourceReferenceDto("gitHub", "other"), RepositoryName = "other"
                },
            ],
        };
        canonical = canonical with { IsActive = false, RepoFilters = [canonical.RepoFilters[0] with { TargetBranchPatterns = ["release/*"] }] };
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(crawlConfigurations: SubstituteCrawlRepository(generic, canonical), synchronization: synchronization);

        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotSubmittable, (await sut.HandleAsync(Command())).Outcome);
        await synchronization.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("refs/heads/MAIN")]
    [InlineData("release/v1")]
    public async Task HandleAsync_MatchingCanonicalTargetAcceptsFreshDestinationBranch(string branch)
    {
        var target = GitHubConfiguration();
        target = target with { RepoFilters = [target.RepoFilters[0] with { TargetBranchPatterns = ["main", "release/*"] }] };
        var sut = Handler(
            crawlConfigurations: SubstituteCrawlRepository(target), queryService: SubstituteQueryService(OpenPullRequest() with { TargetBranch = branch }));
        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, (await sut.HandleAsync(Command())).Outcome);
    }

    [Fact]
    public async Task HandleAsync_EmptyCanonicalTargetPolicyPreservesLegacyUnavailableBranchSubmission()
    {
        var sut = Handler(queryService: SubstituteQueryService(OpenPullRequest() with { TargetBranch = null }));
        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, (await sut.HandleAsync(Command())).Outcome);
    }

    [Theory]
    [InlineData("feature/unselected")]
    [InlineData(null)]
    public async Task HandleAsync_RestrictedCanonicalTargetRefusesFreshBranchBeforeReviewIntake(string? targetBranch)
    {
        var target = GitHubConfiguration();
        target = target with { IsActive = false, RepoFilters = [target.RepoFilters[0] with { TargetBranchPatterns = ["main", "release/*"] }] };
        var broad = GitHubConfiguration() with { Id = Guid.NewGuid(), RepoFilters = [] };
        var query = SubstituteQueryService(OpenPullRequest() with { TargetBranch = targetBranch, ReviewRevision = null }, Revision());
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(crawlConfigurations: SubstituteCrawlRepository(broad, target), queryService: query, synchronization: synchronization);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotSubmittable, result.Outcome);
        await query.Received(1).GetLatestRevisionAsync(ClientId, Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>());
        await synchronization.DidNotReceiveWithAnyArgs().SynchronizeAsync(default!);
    }

    private static readonly Guid ClientId = Guid.Parse("2b0d5c0e-5f6a-4a2b-9a1f-7d4a2c9e1b33");
    private static readonly Guid OtherClientId = Guid.Parse("9f1c7a2d-3e44-4a6b-8f01-5c6d7e8f9a0b");
    private static readonly Guid JobId = Guid.Parse("aa11bb22-cc33-dd44-ee55-ff6677889900");

    [Fact]
    public async Task HandleAsync_WithCoveredCoordinates_SubmitsTheRevisionTheProviderReported()
    {
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(synchronization: synchronization);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, result.Outcome);
        Assert.Equal(JobId, result.JobId);
        await synchronization.Received(1).SynchronizeAsync(
            Arg.Is<PullRequestSynchronizationRequest>(request =>
                request.ClientId == ClientId
                && request.ActivationSource == PullRequestActivationSource.Manual
                && request.ProviderScopePath == "https://github.example.com"
                && request.ProviderProjectKey == "acme"
                && request.RepositoryId == "12345"
                && request.PullRequestId == 7
                && request.PullRequestStatus == PrStatus.Active
                && request.ReviewRevision!.HeadSha == "head-sha"
                && request.ReviewRevision.BaseSha == "base-sha"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenSynchronizationAlsoRetiredAnOlderJob_ReturnsTheNewlyQueuedJobId()
    {
        // Re-reviewing after a push retires the job at the older revision, which the shared synchronization
        // path decides and reports; whether it does so is asserted where that decision is made. What this
        // handler owes the caller is the id of the job now running, not the one that was cancelled.
        var synchronization = SubstituteSynchronization(
            new PullRequestSynchronizationOutcome(
                PullRequestSynchronizationReviewDecision.Submitted,
                PullRequestSynchronizationLifecycleDecision.CancelledActiveJobs,
                ["Cancelled 1 superseded active review job(s) for PR #7.", "Submitted review intake job for PR #7."],
                JobId));
        var sut = Handler(synchronization: synchronization);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, result.Outcome);
        Assert.Equal(JobId, result.JobId);
    }

    [Fact]
    public async Task HandleAsync_WhenAJobIsAlreadyRunningAtThisRevision_ReturnsThatJobWithoutQueueingASecond()
    {
        var synchronization = SubstituteSynchronization(
            new PullRequestSynchronizationOutcome(
                PullRequestSynchronizationReviewDecision.DuplicateActiveJob,
                PullRequestSynchronizationLifecycleDecision.None,
                ["Skipped duplicate active job for PR #7 at revision head-sha."],
                JobId));
        var sut = Handler(synchronization: synchronization);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.DuplicateActiveJob, result.Outcome);
        Assert.Equal(JobId, result.JobId);
    }

    [Fact]
    public async Task HandleAsync_AsksSynchronizationToBypassTheAutomaticLoopGuards()
    {
        // Nothing changed since the last review, and a prior review failed at this very revision, are both
        // reasons the automatic loop stands down. An explicitly requested review is the manual action those
        // guards defer to, so it must not be filtered by either of them.
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(synchronization: synchronization);

        await sut.HandleAsync(Command());

        await synchronization.Received(1).SynchronizeAsync(
            Arg.Is<PullRequestSynchronizationRequest>(request => request.AllowUnchangedResubmission),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenNoConfigurationCoversTheCoordinates_RefusesWithoutCallingTheProvider()
    {
        var queryService = SubstituteQueryService(OpenPullRequest());
        var sut = Handler(crawlConfigurations: SubstituteCrawlRepository(), queryService: queryService);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotAuthorized, result.Outcome);
        Assert.Null(result.JobId);
        await queryService.DidNotReceive()
            .GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://github.other.example.com", "acme")]
    [InlineData("https://github.example.com", "someone-else")]
    public async Task HandleAsync_WhenTheCoordinatesFallOutsideTheConfiguredScope_Refuses(
        string providerScopePath,
        string providerProjectKey)
    {
        // The covering configuration is the authorization boundary: without exact agreement on both the
        // scope path and the project key, a caller could aim the client's credential at another host.
        var sut = Handler();

        var result = await sut.HandleAsync(Command() with { ProviderScopePath = providerScopePath, ProviderProjectKey = providerProjectKey });

        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotAuthorized, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_WhenAnotherClientOwnsTheConfiguration_Refuses()
    {
        var sut = Handler(crawlConfigurations: SubstituteCrawlRepository(GitHubConfiguration() with { ClientId = OtherClientId }));

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotAuthorized, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_WhenTheProviderHasNoSuchPullRequest_ReportsItAsNotFound()
    {
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(queryService: SubstituteQueryService(null), synchronization: synchronization);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.PullRequestNotFound, result.Outcome);
        await synchronization.DidNotReceive()
            .SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenTheProviderCannotBeReached_ReportsAnUnresolvableRevisionWithoutTheException()
    {
        var queryService = Substitute.For<ICodeReviewQueryService>();
        queryService.GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>())
            .Returns<ReviewDiscoveryItemDto?>(_ => throw new InvalidOperationException("credential expired"));
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(queryService: queryService, synchronization: synchronization);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.RevisionUnresolvable, result.Outcome);
        Assert.NotNull(result.Reason);
        Assert.DoesNotContain("credential expired", result.Reason, StringComparison.Ordinal);
        await synchronization.DidNotReceive()
            .SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenTheFollowUpRevisionCallThrows_ReportsAnUnresolvableRevision()
    {
        // The second call reaches the provider exactly as the first does, and fails the same ways.
        var queryService = Substitute.For<ICodeReviewQueryService>();
        queryService.Provider.Returns(ScmProvider.GitHub);
        queryService.GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>())
            .Returns(OpenPullRequest() with { ReviewRevision = null });
        queryService.GetLatestRevisionAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>())
            .Returns<ReviewRevision?>(_ => throw new InvalidOperationException("gateway timeout"));
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(queryService: queryService, synchronization: synchronization);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.RevisionUnresolvable, result.Outcome);
        Assert.DoesNotContain("gateway timeout", result.Reason!, StringComparison.Ordinal);
        await synchronization.DidNotReceive()
            .SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenQueueingTheReviewFails_ReportsASubmissionFailureRatherThanEscaping()
    {
        // The pull request and its revision resolved, so this failure is ours. It is named rather than
        // thrown, because the caller renders every other answer and would have nothing to show for this one.
        var synchronization = Substitute.For<IPullRequestSynchronizationService>();
        synchronization
            .SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>())
            .Returns<PullRequestSynchronizationOutcome>(_ => throw new InvalidOperationException("job store is down"));
        var sut = Handler(synchronization: synchronization);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.SubmissionFailed, result.Outcome);
        Assert.Null(result.JobId);
        Assert.NotNull(result.Reason);
        Assert.DoesNotContain("job store is down", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConfigurationNamesOtherRepositoriesByIdentity_Refuses()
    {
        // A configuration that recorded provider identities can answer whether it covers this repository,
        // and is held to that answer: otherwise one covered repository's coordinates would carry a request
        // aimed at any other repository in the same scope.
        var queryService = SubstituteQueryService(OpenPullRequest());
        var sut = Handler(
            crawlConfigurations: SubstituteCrawlRepository(
                GitHubConfiguration() with
                {
                    RepoFilters =
                    [
                        new CrawlRepoFilterDto(
                            Guid.Parse("77777777-7777-7777-7777-777777777777"),
                            "another-repository",
                            [],
                            new CanonicalSourceReferenceDto("gitHub", "98765"),
                            "another-repository"),
                    ],
                }),
            queryService: queryService);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotAuthorized, result.Outcome);
        await queryService.DidNotReceive()
            .GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenTheConfigurationNamesRepositoriesWithoutIdentities_StillSubmits()
    {
        // A webhook is registered by name and records no provider identity, so its filters cannot be
        // checked against an identity at all. Refusing there would turn an unanswerable question into a
        // refusal of requests that are perfectly legitimate.
        var sut = Handler(
            crawlConfigurations: SubstituteCrawlRepository(),
            webhookConfigurations: SubstituteWebhookRepository(GitHubWebhook()));

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_WhenNoRevisionCanBeResolved_ReportsAnUnresolvableRevision()
    {
        var queryService = SubstituteQueryService(OpenPullRequest() with { ReviewRevision = null }, latestRevision: null);
        var sut = Handler(queryService: queryService);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.RevisionUnresolvable, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_WhenTheReviewCarriesNoRevision_AsksTheAdapterUsingItsOwnReference()
    {
        // The reference the adapter returns is the one it can act on; the one built from coordinates is a
        // best effort that the adapter may have corrected while answering.
        var adapterReview = new CodeReviewRef(
            new RepositoryRef(GitHubHost(), "12345", "acme", "acme/propr", "propr"),
            CodeReviewPlatformKind.PullRequest,
            "corrected-7",
            7);
        var queryService = SubstituteQueryService(
            OpenPullRequest() with { ReviewRevision = null, CodeReview = adapterReview },
            latestRevision: Revision());
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(queryService: queryService, synchronization: synchronization);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, result.Outcome);
        await queryService.Received(1).GetLatestRevisionAsync(
            ClientId,
            Arg.Is<CodeReviewRef>(review => review.ExternalReviewId == "corrected-7"),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CodeReviewState.Merged)]
    [InlineData(CodeReviewState.Closed)]
    public async Task HandleAsync_WhenThePullRequestIsNoLongerOpen_RefusesWithTheReason(CodeReviewState state)
    {
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(
            queryService: SubstituteQueryService(OpenPullRequest() with { ReviewState = state }),
            synchronization: synchronization);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotSubmittable, result.Outcome);
        Assert.NotNull(result.Reason);
        await synchronization.DidNotReceive()
            .SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ForADraftPullRequest_StillSubmits()
    {
        var sut = Handler(queryService: SubstituteQueryService(OpenPullRequest() with { ReviewState = CodeReviewState.Draft }));

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, result.Outcome);
    }

    [Fact]
    public async Task HandleAsync_WhenThePullRequestIsBlockedFromProcessing_RefusesWithTheReason()
    {
        // Blocking is decided inside the shared synchronization path, which declines to queue anything and
        // says why. That reason is what the caller has to show.
        var sut = Handler(
            synchronization: SubstituteSynchronization(
                new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.None,
                    PullRequestSynchronizationLifecycleDecision.None,
                    ["Pull request #7 is blocked from review processing; no review job was created."])));

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotSubmittable, result.Outcome);
        Assert.Contains("blocked", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_ForANumericRepositoryId_CarriesOwnerAndNameInTheProjectPath()
    {
        // GitHub and Forgejo address a repository as owner/name and take the name from the last segment of
        // the project path. Leaving the numeric id there makes every provider call a 404.
        var queryService = SubstituteQueryService(OpenPullRequest());
        var sut = Handler(queryService: queryService);

        await sut.HandleAsync(Command());

        await queryService.Received(1).GetReviewAsync(
            ClientId,
            Arg.Is<CodeReviewRef>(review =>
                review.Repository.ExternalRepositoryId == "12345"
                && review.Repository.OwnerOrNamespace == "acme"
                && review.Repository.ProjectPath == "acme/propr"
                && review.Number == 7),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ForAPathStyleRepositoryId_KeepsThePathAndTakesTheNameFromItsLastSegment()
    {
        // What GitLab records as a repository identity is the namespaced path, and that is what pull-request
        // resolution hands back for one. The path already contains the name, so it is used as it stands:
        // dropping to the project key would leave a project path with no repository in it, which reaches the
        // provider intact and only fails later, when the clone URL built from it cannot be fetched.
        var queryService = SubstituteQueryService(OpenPullRequest());
        var registry = SubstituteRegistry(queryService, [GitHubRepository()]);
        var sut = Handler(
            crawlConfigurations: SubstituteCrawlRepository(),
            webhookConfigurations: SubstituteWebhookRepository(GitLabWebhook()),
            providerRegistry: registry,
            queryService: queryService);

        var result = await sut.HandleAsync(
            Command() with
            {
                ProviderScopePath = "http://localhost:8090",
                ProviderProjectKey = "meister-dev",
                RepositoryId = "meister-dev/propr-review-demo-csharp",
            });

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, result.Outcome);
        await queryService.Received(1).GetReviewAsync(
            ClientId,
            Arg.Is<CodeReviewRef>(review =>
                review.Repository.ExternalRepositoryId == "meister-dev/propr-review-demo-csharp"
                && review.Repository.OwnerOrNamespace == "meister-dev"
                && review.Repository.ProjectPath == "meister-dev/propr-review-demo-csharp"
                && review.Repository.RepositoryName == "propr-review-demo-csharp"),
            Arg.Any<CancellationToken>());

        // The path answered the question outright, so nothing was asked of the provider to find it out.
        registry.DidNotReceive().GetRepositoryDiscoveryProvider(Arg.Any<ScmProvider>());
    }

    [Fact]
    public async Task HandleAsync_WhenDiscoveryReportsADifferentIdentityShape_StillMatchesOnTheProjectPath()
    {
        // GitLab discovery answers with a numeric project id while a GitLab review job records the path, so
        // an identity-only comparison finds nothing for the very repository that was asked about.
        var discovered = new RepositoryRef(
            new ProviderHostRef(ScmProvider.GitLab, "http://localhost:8090"),
            "4242",
            "meister-dev",
            "meister-dev/propr-review-demo-csharp");
        var queryService = SubstituteQueryService(OpenPullRequest());
        var sut = Handler(
            crawlConfigurations: SubstituteCrawlRepository(),
            webhookConfigurations: SubstituteWebhookRepository(GitLabWebhook()),
            providerRegistry: SubstituteRegistry(queryService, [discovered]),
            queryService: queryService);

        var result = await sut.HandleAsync(
            Command() with
            {
                ProviderScopePath = "http://localhost:8090",
                ProviderProjectKey = "meister-dev",
                RepositoryId = "propr-review-demo-csharp",
            });

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, result.Outcome);
        await queryService.Received(1).GetReviewAsync(
            ClientId,
            Arg.Is<CodeReviewRef>(review =>
                review.Repository.ExternalRepositoryId == "4242"
                && review.Repository.ProjectPath == "meister-dev/propr-review-demo-csharp"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ForAzureDevOps_KeepsTheProjectAloneInTheProjectPath()
    {
        // Azure DevOps reads the project path as the project itself, both for its API calls and for the
        // clone URL, so the repository name belongs in the name field and nowhere else.
        var queryService = SubstituteQueryService(OpenPullRequest());
        var sut = Handler(
            crawlConfigurations: SubstituteCrawlRepository(AzureDevOpsConfiguration()),
            queryService: queryService);

        await sut.HandleAsync(
            Command() with
            {
                ProviderScopePath = "https://dev.azure.com/meister-dev",
                ProviderProjectKey = "5cda05b9-bbfa-4c44-88e9-16aa900515d2",
                RepositoryId = "c39fd3f3-e84b-4d01-84df-57964de91bc8",
            });

        await queryService.Received(1).GetReviewAsync(
            ClientId,
            Arg.Is<CodeReviewRef>(review =>
                review.Repository.OwnerOrNamespace == "5cda05b9-bbfa-4c44-88e9-16aa900515d2"
                && review.Repository.ProjectPath == "5cda05b9-bbfa-4c44-88e9-16aa900515d2"
                && review.Repository.RepositoryName == "meister-propr"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenTheConfigurationDoesNotNameTheRepository_TakesTheIdentityFromDiscovery()
    {
        // A webhook is registered by name and records no provider identity, so the repository the caller
        // names is covered without being described. The adapter's own reference is authoritative.
        var discovered = new RepositoryRef(GitHubHost(), "12345", "acme", "acme/propr-discovered");
        var queryService = SubstituteQueryService(OpenPullRequest());
        var sut = Handler(
            crawlConfigurations: SubstituteCrawlRepository(),
            webhookConfigurations: SubstituteWebhookRepository(GitHubWebhook()),
            providerRegistry: SubstituteRegistry(queryService, [discovered]),
            queryService: queryService);

        await sut.HandleAsync(Command());

        await queryService.Received(1).GetReviewAsync(
            ClientId,
            Arg.Is<CodeReviewRef>(review => review.Repository.ProjectPath == "acme/propr-discovered"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenDiscoveryFails_StillAsksTheProviderWithTheProjectKey()
    {
        // A discovery failure is not an answer about the pull request. Falling through lets the query
        // service report what it finds instead of inventing a failure before the provider has been asked.
        var queryService = SubstituteQueryService(OpenPullRequest());
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        registry.IsRegistered(Arg.Any<ScmProvider>()).Returns(true);
        registry.GetCodeReviewQueryService(Arg.Any<ScmProvider>()).Returns(queryService);
        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        discovery
            .ListRepositoriesAsync(Arg.Any<Guid>(), Arg.Any<ProviderHostRef>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<RepositoryRef>>(_ => throw new InvalidOperationException("host unreachable"));
        registry.GetRepositoryDiscoveryProvider(Arg.Any<ScmProvider>()).Returns(discovery);

        var sut = Handler(
            crawlConfigurations: SubstituteCrawlRepository(),
            webhookConfigurations: SubstituteWebhookRepository(GitHubWebhook()),
            providerRegistry: registry,
            queryService: queryService);

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, result.Outcome);
        await queryService.Received(1).GetReviewAsync(
            ClientId,
            Arg.Is<CodeReviewRef>(review =>
                review.Repository.OwnerOrNamespace == "acme" && review.Repository.ProjectPath == "acme"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AppliesTheCoveringConfigurationSourceScopeAndTemperature()
    {
        // A manually requested review has to produce the review the configuration describes, not a
        // differently configured one.
        var sourceId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        var synchronization = SubstituteSynchronization(Submitted());
        var sut = Handler(
            crawlConfigurations: SubstituteCrawlRepository(
                GitHubConfiguration() with
                {
                    ProCursorSourceScopeMode = ProCursorSourceScopeMode.SelectedSources,
                    ProCursorSourceIds = [sourceId],
                    ReviewTemperature = 0.25f,
                }),
            synchronization: synchronization);

        await sut.HandleAsync(Command());

        await synchronization.Received(1).SynchronizeAsync(
            Arg.Is<PullRequestSynchronizationRequest>(request =>
                request.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources
                && request.ProCursorSourceIds.Contains(sourceId)
                && request.ReviewTemperature == 0.25f),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenTheSelectedSourceScopeIsUnusable_RefusesWithTheReason()
    {
        var sut = Handler(
            synchronization: SubstituteSynchronization(
                new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.EmptySourceScope,
                    PullRequestSynchronizationLifecycleDecision.None,
                    ["Skipped review intake for PR #7 because the selected ProCursor source scope is empty."])));

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.NotSubmittable, result.Outcome);
        Assert.Contains("source scope", result.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleAsync_WhenOnlyAWebhookCoversTheCoordinates_StillSubmits()
    {
        var sut = Handler(
            crawlConfigurations: SubstituteCrawlRepository(),
            webhookConfigurations: SubstituteWebhookRepository(GitHubWebhook()));

        var result = await sut.HandleAsync(Command());

        Assert.Equal(SubmitReviewByCoordinatesOutcome.Submitted, result.Outcome);
    }

    /// <summary>Builds the handler over a GitHub crawl configuration that covers the fixture coordinates.</summary>
    private static SubmitReviewByCoordinatesHandler Handler(
        ICrawlConfigurationRepository? crawlConfigurations = null,
        IWebhookConfigurationRepository? webhookConfigurations = null,
        IScmProviderRegistry? providerRegistry = null,
        ICodeReviewQueryService? queryService = null,
        IPullRequestSynchronizationService? synchronization = null)
    {
        var effectiveQueryService = queryService ?? SubstituteQueryService(OpenPullRequest());
        var effectiveSynchronization = synchronization ?? SubstituteSynchronization(Submitted());
        effectiveSynchronization.PrepareAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new PreparedPullRequestSynchronization(
                call.Arg<PullRequestSynchronizationRequest>(),
                (authorized, ct) => effectiveSynchronization.SynchronizeAsync(authorized, ct)));

        return new SubmitReviewByCoordinatesHandler(
            crawlConfigurations ?? SubstituteCrawlRepository(GitHubConfiguration()),
            webhookConfigurations ?? SubstituteWebhookRepository(),
            providerRegistry ?? SubstituteRegistry(effectiveQueryService, [GitHubRepository()]),
            effectiveSynchronization,
            NullLogger<SubmitReviewByCoordinatesHandler>.Instance);
    }

    private static SubmitReviewByCoordinatesCommand Command()
    {
        return new SubmitReviewByCoordinatesCommand(
            ClientId,
            "https://github.example.com",
            "acme",
            "12345",
            7);
    }

    private static ProviderHostRef GitHubHost()
    {
        return new ProviderHostRef(ScmProvider.GitHub, "https://github.example.com");
    }

    private static RepositoryRef GitHubRepository()
    {
        return new RepositoryRef(GitHubHost(), "12345", "acme", "acme/propr");
    }

    private static ReviewRevision Revision()
    {
        return new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
    }

    private static ReviewDiscoveryItemDto OpenPullRequest()
    {
        var repository = GitHubRepository();

        return new ReviewDiscoveryItemDto(
            ScmProvider.GitHub,
            repository,
            new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "7", 7),
            CodeReviewState.Open,
            Revision(),
            null,
            "Add coordinate-addressed review intake",
            "https://github.example.com/acme/propr/pull/7",
            "feature/intake",
            "main");
    }

    private static PullRequestSynchronizationOutcome Submitted()
    {
        return new PullRequestSynchronizationOutcome(
            PullRequestSynchronizationReviewDecision.Submitted,
            PullRequestSynchronizationLifecycleDecision.None,
            ["Submitted review intake job for PR #7."],
            JobId);
    }

    private static IPullRequestSynchronizationService SubstituteSynchronization(PullRequestSynchronizationOutcome outcome)
    {
        var synchronization = Substitute.For<IPullRequestSynchronizationService>();
        synchronization
            .SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(outcome);
        return synchronization;
    }

    private static ICodeReviewQueryService SubstituteQueryService(
        ReviewDiscoveryItemDto? review,
        ReviewRevision? latestRevision = null)
    {
        var queryService = Substitute.For<ICodeReviewQueryService>();
        queryService.Provider.Returns(ScmProvider.GitHub);
        queryService.GetReviewAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>())
            .Returns(review);
        queryService.GetLatestRevisionAsync(Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<CancellationToken>())
            .Returns(latestRevision);
        return queryService;
    }

    private static IScmProviderRegistry SubstituteRegistry(
        ICodeReviewQueryService queryService,
        IReadOnlyList<RepositoryRef> discoverableRepositories)
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        registry.IsRegistered(Arg.Any<ScmProvider>()).Returns(true);
        registry.GetCodeReviewQueryService(Arg.Any<ScmProvider>()).Returns(queryService);

        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        discovery
            .ListRepositoriesAsync(Arg.Any<Guid>(), Arg.Any<ProviderHostRef>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(discoverableRepositories);
        registry.GetRepositoryDiscoveryProvider(Arg.Any<ScmProvider>()).Returns(discovery);
        return registry;
    }

    private static void FailCoverageRead(ICrawlConfigurationRepository repository, IWebhookConfigurationRepository webhooks, string read, Exception failure)
    {
        if (read == "management")
        {
            repository.GetManagementTargetsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns<IReadOnlyList<CrawlConfigurationDto>>(_ => throw failure);
        }
        else if (read == "webhook")
        {
            webhooks.GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
                .Returns<IReadOnlyList<WebhookConfigurationDto>>(_ => throw failure);
        }
        else
        {
            repository.GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
                .Returns<IReadOnlyList<CrawlConfigurationDto>>(_ => throw failure);
        }
    }

    private static ICrawlConfigurationRepository SubstituteCrawlRepository(params CrawlConfigurationDto[] configurations)
    {
        var repository = Substitute.For<ICrawlConfigurationRepository>();
        repository.GetManagementTargetsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(configurations);
        repository.AcquireReviewTargetAdmissionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Substitute.For<IAsyncDisposable>());
        repository
            .GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(configurations);
        return repository;
    }

    private static IWebhookConfigurationRepository SubstituteWebhookRepository(params WebhookConfigurationDto[] configurations)
    {
        var repository = Substitute.For<IWebhookConfigurationRepository>();
        repository
            .GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(configurations);
        return repository;
    }

    private static CrawlConfigurationDto GitHubConfiguration()
    {
        return new CrawlConfigurationDto(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ClientId,
            ScmProvider.GitHub,
            "https://github.example.com",
            "acme",
            300,
            true,
            DateTimeOffset.UnixEpoch,
            [
                new CrawlRepoFilterDto(
                    Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    "propr",
                    [],
                    new CanonicalSourceReferenceDto("gitHub", "12345"),
                    "propr"),
            ]);
    }

    private static CrawlConfigurationDto AzureDevOpsConfiguration()
    {
        return new CrawlConfigurationDto(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ClientId,
            ScmProvider.AzureDevOps,
            "https://dev.azure.com/meister-dev",
            "5cda05b9-bbfa-4c44-88e9-16aa900515d2",
            300,
            true,
            DateTimeOffset.UnixEpoch,
            [
                new CrawlRepoFilterDto(
                    Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    "meister-propr",
                    [],
                    new CanonicalSourceReferenceDto("azureDevOps", "c39fd3f3-e84b-4d01-84df-57964de91bc8"),
                    "meister-propr"),
            ]);
    }

    /// <summary>A GitLab webhook covering the demo namespace, registered by name and recording no identity.</summary>
    private static WebhookConfigurationDto GitLabWebhook()
    {
        return new WebhookConfigurationDto(
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            ClientId,
            WebhookProviderType.GitLab,
            "path-key",
            "http://localhost:8090",
            "meister-dev",
            true,
            DateTimeOffset.UnixEpoch,
            [WebhookEventType.PullRequestCreated],
            [
                new WebhookRepoFilterDto(
                    Guid.Parse("99999999-9999-9999-9999-999999999999"),
                    "propr-review-demo-csharp",
                    [],
                    null,
                    "propr-review-demo-csharp"),
            ]);
    }

    private static WebhookConfigurationDto GitHubWebhook()
    {
        return new WebhookConfigurationDto(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            ClientId,
            WebhookProviderType.GitHub,
            "path-key",
            "https://github.example.com",
            "acme",
            true,
            DateTimeOffset.UnixEpoch,
            [WebhookEventType.PullRequestCreated],
            [
                new WebhookRepoFilterDto(
                    Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    "propr",
                    [],
                    null,
                    "propr"),
            ]);
    }
}
