// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.DTOs.AzureDevOps;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewByCoordinates;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Intake.Commands;

public sealed class SubmitReviewByCoordinatesHandlerTests
{
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
        var registry = Substitute.For<IScmProviderRegistry>();
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

        return new SubmitReviewByCoordinatesHandler(
            crawlConfigurations ?? SubstituteCrawlRepository(GitHubConfiguration()),
            webhookConfigurations ?? SubstituteWebhookRepository(),
            providerRegistry ?? SubstituteRegistry(effectiveQueryService, [GitHubRepository()]),
            synchronization ?? SubstituteSynchronization(Submitted()),
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
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.IsRegistered(Arg.Any<ScmProvider>()).Returns(true);
        registry.GetCodeReviewQueryService(Arg.Any<ScmProvider>()).Returns(queryService);

        var discovery = Substitute.For<IRepositoryDiscoveryProvider>();
        discovery
            .ListRepositoriesAsync(Arg.Any<Guid>(), Arg.Any<ProviderHostRef>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(discoverableRepositories);
        registry.GetRepositoryDiscoveryProvider(Arg.Any<ScmProvider>()).Returns(discovery);
        return registry;
    }

    private static ICrawlConfigurationRepository SubstituteCrawlRepository(params CrawlConfigurationDto[] configurations)
    {
        var repository = Substitute.For<ICrawlConfigurationRepository>();
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
