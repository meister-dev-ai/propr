// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services;

public sealed class ReviewWorkflowRunnerTests
{
    [Fact]
    public async Task RunAsync_UsesReviewStrategyDispatcherForOfflineExecution()
    {
        var jobRepository = Substitute.For<IJobRepository>();
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var diagnosticsReader = Substitute.For<IReviewDiagnosticsReader>();
        var fixtureAccessor = Substitute.For<IReviewEvaluationFixtureAccessor>();
        var fixtureValidator = Substitute.For<IReviewEvaluationFixtureValidator>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        var reviewStrategyDispatcher = Substitute.For<IReviewStrategyDispatcher>();

        var fixture = CreateFixture();
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            fixture.PullRequestSnapshot.CodeReview.Repository.Host.HostBaseUrl,
            fixture.PullRequestSnapshot.CodeReview.Repository.ProjectPath,
            fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
            fixture.PullRequestSnapshot.CodeReview.Number,
            1);
        job.SelectReviewStrategy(
            ReviewStrategy.PrWideAgentic,
            ReviewStrategySelectionSource.JobOverride,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);

        var configuration = new EvaluationConfiguration(
            "baseline",
            new EvaluationModelSelection(["gpt-4o"]),
            new EvaluationOutputOptions("artifacts/run.json", "full"),
            EnableProRV: false);
        var request = new ReviewWorkflowRequest(job, Substitute.For<IChatClient>(), "gpt-4o", fixture, configuration);
        var pullRequest = CreatePullRequest();
        var reviewTools = Substitute.For<IReviewContextTools>();
        var expectedReviewResult = new ReviewResult("strategy-dispatched", []);

        jobs.GetById(job.Id).Returns(job);
        pullRequestFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                null,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(reviewTools);
        instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns([]);
        exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.Default);
        diagnosticsReader.GetJobProtocolAsync(job.Id, ct: Arg.Any<CancellationToken>())
            .Returns(new GetReviewJobProtocolResult(job.Id, []));
        reviewStrategyDispatcher.ReviewAsync(
                job,
                pullRequest,
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                request.ChatClient)
            .Returns(expectedReviewResult);

        var sut = new ReviewWorkflowRunner(
            jobRepository,
            jobs,
            diagnosticsReader,
            fixtureAccessor,
            fixtureValidator,
            pullRequestFetcher,
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            reviewStrategyDispatcher);

        var result = await sut.RunAsync(request, CancellationToken.None);

        Assert.Same(expectedReviewResult, result.FinalResult);
        await reviewStrategyDispatcher.Received(1).ReviewAsync(
            job,
            pullRequest,
            Arg.Is<ReviewSystemContext>(context =>
                context.ModelId == "gpt-4o"
                && context.Temperature == null
                && !context.EnableProRV
                && context.AugmentationMode == ReviewAugmentationMode.Disabled),
            Arg.Any<CancellationToken>(),
            request.ChatClient);
        Assert.Equal(ReviewAugmentationMode.Disabled, result.AugmentationMode);
    }

    [Fact]
    public async Task RunAsync_WhenJobHasSelectedProCursorSources_ForwardsSourceScopeToReviewTools()
    {
        var jobRepository = Substitute.For<IJobRepository>();
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var diagnosticsReader = Substitute.For<IReviewDiagnosticsReader>();
        var fixtureAccessor = Substitute.For<IReviewEvaluationFixtureAccessor>();
        var fixtureValidator = Substitute.For<IReviewEvaluationFixtureValidator>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        var reviewStrategyDispatcher = Substitute.For<IReviewStrategyDispatcher>();

        var fixture = CreateFixture();
        var selectedSourceId = Guid.NewGuid();
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            fixture.PullRequestSnapshot.CodeReview.Repository.Host.HostBaseUrl,
            fixture.PullRequestSnapshot.CodeReview.Repository.ProjectPath,
            fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
            fixture.PullRequestSnapshot.CodeReview.Number,
            1);
        job.SetProviderReviewContext(fixture.PullRequestSnapshot.CodeReview);
        job.SetProCursorSourceScope(ProCursorSourceScopeMode.SelectedSources, [selectedSourceId]);

        var request = new ReviewWorkflowRequest(job, Substitute.For<IChatClient>(), "gpt-4o", fixture);
        var pullRequest = CreatePullRequest();
        var reviewTools = Substitute.For<IReviewContextTools>();

        jobs.GetById(job.Id).Returns(job);
        pullRequestFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                null,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(reviewTools);
        instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns([]);
        exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.Default);
        diagnosticsReader.GetJobProtocolAsync(job.Id, ct: Arg.Any<CancellationToken>())
            .Returns(new GetReviewJobProtocolResult(job.Id, []));
        reviewStrategyDispatcher.ReviewAsync(
                job,
                pullRequest,
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                request.ChatClient)
            .Returns(new ReviewResult("strategy-dispatched", []));

        var sut = new ReviewWorkflowRunner(
            jobRepository,
            jobs,
            diagnosticsReader,
            fixtureAccessor,
            fixtureValidator,
            pullRequestFetcher,
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            reviewStrategyDispatcher);

        await sut.RunAsync(request, CancellationToken.None);

        reviewContextToolsFactory.Received(1).Create(
            Arg.Is<ReviewContextToolsRequest>(toolsRequest => HasSelectedKnowledgeSource(toolsRequest, job.ClientId, selectedSourceId)));
    }

    [Fact]
    public async Task RunAsync_WithoutConfiguration_DefaultsProRvToDisabled()
    {
        var jobRepository = Substitute.For<IJobRepository>();
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var diagnosticsReader = Substitute.For<IReviewDiagnosticsReader>();
        var fixtureAccessor = Substitute.For<IReviewEvaluationFixtureAccessor>();
        var fixtureValidator = Substitute.For<IReviewEvaluationFixtureValidator>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        var reviewStrategyDispatcher = Substitute.For<IReviewStrategyDispatcher>();

        var fixture = CreateFixture();
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            fixture.PullRequestSnapshot.CodeReview.Repository.Host.HostBaseUrl,
            fixture.PullRequestSnapshot.CodeReview.Repository.ProjectPath,
            fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
            fixture.PullRequestSnapshot.CodeReview.Number,
            1);
        var request = new ReviewWorkflowRequest(job, Substitute.For<IChatClient>(), "gpt-4o", fixture);
        var pullRequest = CreatePullRequest();
        var reviewTools = Substitute.For<IReviewContextTools>();

        jobs.GetById(job.Id).Returns(job);
        pullRequestFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                null,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(reviewTools);
        instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns([]);
        exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.Default);
        diagnosticsReader.GetJobProtocolAsync(job.Id, ct: Arg.Any<CancellationToken>())
            .Returns(new GetReviewJobProtocolResult(job.Id, []));
        reviewStrategyDispatcher.ReviewAsync(
                job,
                pullRequest,
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                request.ChatClient)
            .Returns(new ReviewResult("strategy-dispatched", []));

        var sut = new ReviewWorkflowRunner(
            jobRepository,
            jobs,
            diagnosticsReader,
            fixtureAccessor,
            fixtureValidator,
            pullRequestFetcher,
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            reviewStrategyDispatcher);

        var result = await sut.RunAsync(request, CancellationToken.None);

        await reviewStrategyDispatcher.Received(1).ReviewAsync(
            job,
            pullRequest,
            Arg.Is<ReviewSystemContext>(context =>
                !context.EnableProRV &&
                context.AugmentationMode == ReviewAugmentationMode.Disabled),
            Arg.Any<CancellationToken>(),
            request.ChatClient);
        Assert.Equal(ReviewAugmentationMode.Disabled, result.AugmentationMode);
    }

    private static bool HasSelectedKnowledgeSource(ReviewContextToolsRequest request, Guid clientId, Guid sourceId)
    {
        return request.ClientId == clientId
               && request.KnowledgeSourceIds is not null
               && request.KnowledgeSourceIds.SequenceEqual(new[] { sourceId });
    }

    [Fact]
    public async Task RunAsync_WithLateAugmentationConfiguration_UsesLateAugmentationContext()
    {
        var jobRepository = Substitute.For<IJobRepository>();
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var diagnosticsReader = Substitute.For<IReviewDiagnosticsReader>();
        var fixtureAccessor = Substitute.For<IReviewEvaluationFixtureAccessor>();
        var fixtureValidator = Substitute.For<IReviewEvaluationFixtureValidator>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        var reviewStrategyDispatcher = Substitute.For<IReviewStrategyDispatcher>();

        var fixture = CreateFixture();
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            fixture.PullRequestSnapshot.CodeReview.Repository.Host.HostBaseUrl,
            fixture.PullRequestSnapshot.CodeReview.Repository.ProjectPath,
            fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
            fixture.PullRequestSnapshot.CodeReview.Number,
            1);
        var configuration = new EvaluationConfiguration(
            "late-steering",
            new EvaluationModelSelection(["gpt-4o"]),
            new EvaluationOutputOptions("artifacts/run.json", "full"),
            EnableProRV: true,
            AugmentationMode: ReviewAugmentationMode.LateAugmentation);
        var request = new ReviewWorkflowRequest(job, Substitute.For<IChatClient>(), "gpt-4o", fixture, configuration);
        var pullRequest = CreatePullRequest();
        var reviewTools = Substitute.For<IReviewContextTools>();
        var expectedReviewResult = new ReviewResult("strategy-dispatched", []);

        jobs.GetById(job.Id).Returns(job);
        pullRequestFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                null,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(reviewTools);
        instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns([]);
        exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.Default);
        diagnosticsReader.GetJobProtocolAsync(job.Id, ct: Arg.Any<CancellationToken>())
            .Returns(new GetReviewJobProtocolResult(job.Id, []));
        reviewStrategyDispatcher.ReviewAsync(
                job,
                pullRequest,
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                request.ChatClient)
            .Returns(expectedReviewResult);

        var sut = new ReviewWorkflowRunner(
            jobRepository,
            jobs,
            diagnosticsReader,
            fixtureAccessor,
            fixtureValidator,
            pullRequestFetcher,
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            reviewStrategyDispatcher);

        var result = await sut.RunAsync(request, CancellationToken.None);

        await reviewStrategyDispatcher.Received(1).ReviewAsync(
            job,
            pullRequest,
            Arg.Is<ReviewSystemContext>(context =>
                context.EnableProRV &&
                context.AugmentationMode == ReviewAugmentationMode.LateAugmentation),
            Arg.Any<CancellationToken>(),
            request.ChatClient);
        Assert.Equal(ReviewAugmentationMode.LateAugmentation, result.AugmentationMode);
    }

    [Fact]
    public async Task RunAsync_RequestAugmentationModeOverridesConfiguration()
    {
        var jobRepository = Substitute.For<IJobRepository>();
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var diagnosticsReader = Substitute.For<IReviewDiagnosticsReader>();
        var fixtureAccessor = Substitute.For<IReviewEvaluationFixtureAccessor>();
        var fixtureValidator = Substitute.For<IReviewEvaluationFixtureValidator>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        var reviewStrategyDispatcher = Substitute.For<IReviewStrategyDispatcher>();

        var fixture = CreateFixture();
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            fixture.PullRequestSnapshot.CodeReview.Repository.Host.HostBaseUrl,
            fixture.PullRequestSnapshot.CodeReview.Repository.ProjectPath,
            fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
            fixture.PullRequestSnapshot.CodeReview.Number,
            1);
        var configuration = new EvaluationConfiguration(
            "baseline",
            new EvaluationModelSelection(["gpt-4o"]),
            new EvaluationOutputOptions("artifacts/run.json", "full"),
            AugmentationMode: ReviewAugmentationMode.Disabled);
        var request = new ReviewWorkflowRequest(
            job,
            Substitute.For<IChatClient>(),
            "gpt-4o",
            fixture,
            configuration,
            AugmentationMode: ReviewAugmentationMode.LateAugmentation);
        var pullRequest = CreatePullRequest();
        var reviewTools = Substitute.For<IReviewContextTools>();
        var expectedReviewResult = new ReviewResult("strategy-dispatched", []);

        jobs.GetById(job.Id).Returns(job);
        pullRequestFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                null,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(reviewTools);
        instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns([]);
        exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.Default);
        diagnosticsReader.GetJobProtocolAsync(job.Id, ct: Arg.Any<CancellationToken>())
            .Returns(new GetReviewJobProtocolResult(job.Id, []));
        reviewStrategyDispatcher.ReviewAsync(
                job,
                pullRequest,
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                request.ChatClient)
            .Returns(expectedReviewResult);

        var sut = new ReviewWorkflowRunner(
            jobRepository,
            jobs,
            diagnosticsReader,
            fixtureAccessor,
            fixtureValidator,
            pullRequestFetcher,
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            reviewStrategyDispatcher);

        var result = await sut.RunAsync(request, CancellationToken.None);

        await reviewStrategyDispatcher.Received(1).ReviewAsync(
            job,
            pullRequest,
            Arg.Is<ReviewSystemContext>(context => context.AugmentationMode == ReviewAugmentationMode.LateAugmentation),
            Arg.Any<CancellationToken>(),
            request.ChatClient);
        Assert.Equal(ReviewAugmentationMode.LateAugmentation, result.AugmentationMode);
    }

    [Fact]
    public async Task RunAsync_WithActiveScenario_SetsAndClearsScenarioOnAccessor()
    {
        var jobRepository = Substitute.For<IJobRepository>();
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var diagnosticsReader = Substitute.For<IReviewDiagnosticsReader>();
        var fixtureAccessor = Substitute.For<IReviewEvaluationFixtureAccessor>();
        var fixtureValidator = Substitute.For<IReviewEvaluationFixtureValidator>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        var reviewStrategyDispatcher = Substitute.For<IReviewStrategyDispatcher>();

        var fixture = CreateFixture().WithScenario("instructions-good");
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            fixture.PullRequestSnapshot.CodeReview.Repository.Host.HostBaseUrl,
            fixture.PullRequestSnapshot.CodeReview.Repository.ProjectPath,
            fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
            fixture.PullRequestSnapshot.CodeReview.Number,
            1);
        var request = new ReviewWorkflowRequest(job, Substitute.For<IChatClient>(), "gpt-4o", fixture);
        var pullRequest = CreatePullRequest();
        var reviewTools = Substitute.For<IReviewContextTools>();

        jobs.GetById(job.Id).Returns(job);
        pullRequestFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                null,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(reviewTools);
        instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns([]);
        exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.Default);
        diagnosticsReader.GetJobProtocolAsync(job.Id, ct: Arg.Any<CancellationToken>())
            .Returns(new GetReviewJobProtocolResult(job.Id, []));
        reviewStrategyDispatcher.ReviewAsync(
                job,
                pullRequest,
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                request.ChatClient)
            .Returns(new ReviewResult("strategy-dispatched", []));

        var sut = new ReviewWorkflowRunner(
            jobRepository,
            jobs,
            diagnosticsReader,
            fixtureAccessor,
            fixtureValidator,
            pullRequestFetcher,
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            reviewStrategyDispatcher);

        await sut.RunAsync(request, CancellationToken.None);

        Received.InOrder(() =>
        {
            fixtureAccessor.Fixture = fixture;
            fixtureAccessor.ScenarioId = "instructions-good";
            fixtureAccessor.ScenarioId = null;
            fixtureAccessor.Fixture = null;
        });
    }

    [Fact]
    public async Task RunAsync_WithExplicitPipelineProfile_ForwardsProfileToDispatcher()
    {
        var jobRepository = Substitute.For<IJobRepository>();
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var diagnosticsReader = Substitute.For<IReviewDiagnosticsReader>();
        var fixtureAccessor = Substitute.For<IReviewEvaluationFixtureAccessor>();
        var fixtureValidator = Substitute.For<IReviewEvaluationFixtureValidator>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        var reviewStrategyDispatcher = Substitute.For<IReviewStrategyDispatcher>();

        var fixture = CreateFixture();
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            fixture.PullRequestSnapshot.CodeReview.Repository.Host.HostBaseUrl,
            fixture.PullRequestSnapshot.CodeReview.Repository.ProjectPath,
            fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
            fixture.PullRequestSnapshot.CodeReview.Number,
            1);
        job.SelectReviewStrategy(
            new ReviewStrategySelection(
                ReviewStrategy.AgenticFileByFile,
                ReviewStrategySelectionSource.JobOverride,
                ReviewComparisonMode.Single,
                ReviewPublicationMode.Publish,
                null,
                "agentic-experimental"));

        var request = new ReviewWorkflowRequest(
            job,
            Substitute.For<IChatClient>(),
            "gpt-4o",
            fixture,
            new EvaluationConfiguration(
                "baseline",
                new EvaluationModelSelection(["gpt-4o"]),
                new EvaluationOutputOptions("artifacts/run.json", "full")),
            "agentic-experimental");
        var pullRequest = CreatePullRequest();
        var reviewTools = Substitute.For<IReviewContextTools>();
        var expectedReviewResult = new ReviewResult("strategy-dispatched", []);

        jobs.GetById(job.Id).Returns(job);
        pullRequestFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                null,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(reviewTools);
        instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns([]);
        exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.Default);
        diagnosticsReader.GetJobProtocolAsync(job.Id, ct: Arg.Any<CancellationToken>())
            .Returns(new GetReviewJobProtocolResult(job.Id, []));
        reviewStrategyDispatcher.ReviewAsync(
                job,
                pullRequest,
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                request.ChatClient,
                "agentic-experimental")
            .Returns(expectedReviewResult);

        var sut = new ReviewWorkflowRunner(
            jobRepository,
            jobs,
            diagnosticsReader,
            fixtureAccessor,
            fixtureValidator,
            pullRequestFetcher,
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            reviewStrategyDispatcher);

        var result = await sut.RunAsync(request, CancellationToken.None);

        Assert.Same(expectedReviewResult, result.FinalResult);
        await reviewStrategyDispatcher.Received(1).ReviewAsync(
            job,
            pullRequest,
            Arg.Any<ReviewSystemContext>(),
            Arg.Any<CancellationToken>(),
            request.ChatClient,
            "agentic-experimental");
    }

    [Fact]
    public async Task RunAsync_ForwardsSkippedStepsIntoReviewSystemContext()
    {
        var jobRepository = Substitute.For<IJobRepository>();
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var diagnosticsReader = Substitute.For<IReviewDiagnosticsReader>();
        var fixtureAccessor = Substitute.For<IReviewEvaluationFixtureAccessor>();
        var fixtureValidator = Substitute.For<IReviewEvaluationFixtureValidator>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        var reviewStrategyDispatcher = Substitute.For<IReviewStrategyDispatcher>();

        var fixture = CreateFixture();
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            fixture.PullRequestSnapshot.CodeReview.Repository.Host.HostBaseUrl,
            fixture.PullRequestSnapshot.CodeReview.Repository.ProjectPath,
            fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
            fixture.PullRequestSnapshot.CodeReview.Number,
            1);
        var request = new ReviewWorkflowRequest(
            job,
            Substitute.For<IChatClient>(),
            "gpt-4o",
            fixture,
            SkippedSteps: new ReviewStepSkips([FileByFileReviewStepIds.PrVerification]));
        var pullRequest = CreatePullRequest();
        var reviewTools = Substitute.For<IReviewContextTools>();

        jobs.GetById(job.Id).Returns(job);
        pullRequestFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                null,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(reviewTools);
        instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns([]);
        exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.Default);
        diagnosticsReader.GetJobProtocolAsync(job.Id, ct: Arg.Any<CancellationToken>())
            .Returns(new GetReviewJobProtocolResult(job.Id, []));
        reviewStrategyDispatcher.ReviewAsync(
                job,
                pullRequest,
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                request.ChatClient)
            .Returns(new ReviewResult("strategy-dispatched", []));

        var sut = new ReviewWorkflowRunner(
            jobRepository,
            jobs,
            diagnosticsReader,
            fixtureAccessor,
            fixtureValidator,
            pullRequestFetcher,
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            reviewStrategyDispatcher);

        await sut.RunAsync(request, CancellationToken.None);

        await reviewStrategyDispatcher.Received(1).ReviewAsync(
            job,
            pullRequest,
            Arg.Is<ReviewSystemContext>(context => context.SkippedSteps.Contains(FileByFileReviewStepIds.PrVerification)),
            Arg.Any<CancellationToken>(),
            request.ChatClient);
    }

    [Fact]
    public async Task RunAsync_CarriesBoundaryIssuesIntoWorkflowResult()
    {
        var jobRepository = Substitute.For<IJobRepository>();
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var diagnosticsReader = Substitute.For<IReviewDiagnosticsReader>();
        var fixtureAccessor = Substitute.For<IReviewEvaluationFixtureAccessor>();
        var fixtureValidator = Substitute.For<IReviewEvaluationFixtureValidator>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        var reviewStrategyDispatcher = Substitute.For<IReviewStrategyDispatcher>();
        var boundaryIssue = BoundaryIssueReport.CreateDeferred(
            "reviewing.test-boundary-issue",
            "ReviewWorkflowRunnerTests",
            BoundaryIssueReport.IssueTypes.OwnershipDrift,
            "Test boundary issue used to verify workflow result passthrough.",
            "Reviewing");

        var fixture = CreateFixture();
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            fixture.PullRequestSnapshot.CodeReview.Repository.Host.HostBaseUrl,
            fixture.PullRequestSnapshot.CodeReview.Repository.ProjectPath,
            fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
            fixture.PullRequestSnapshot.CodeReview.Number,
            1);
        var request = new ReviewWorkflowRequest(job, Substitute.For<IChatClient>(), "gpt-4o", fixture);
        var pullRequest = CreatePullRequest();
        var reviewTools = Substitute.For<IReviewContextTools>();
        var expectedReviewResult = new ReviewResult("strategy-dispatched", []);

        jobs.GetById(job.Id).Returns(job);
        pullRequestFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                null,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(reviewTools);
        instructionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns([]);
        exclusionFetcher.FetchAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                pullRequest.TargetBranch,
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.Default);
        diagnosticsReader.GetJobProtocolAsync(job.Id, ct: Arg.Any<CancellationToken>())
            .Returns(new GetReviewJobProtocolResult(job.Id, []));
        reviewStrategyDispatcher.ReviewAsync(
                job,
                pullRequest,
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                request.ChatClient)
            .Returns(expectedReviewResult);

        var sut = new ReviewWorkflowRunner(
            jobRepository,
            jobs,
            diagnosticsReader,
            fixtureAccessor,
            fixtureValidator,
            pullRequestFetcher,
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            reviewStrategyDispatcher,
            [boundaryIssue]);

        var result = await sut.RunAsync(request, CancellationToken.None);

        var issue = Assert.Single(result.BoundaryIssues);
        Assert.Equal(boundaryIssue.IssueId, issue.IssueId);
    }

    private static ReviewEvaluationFixture CreateFixture()
    {
        return new ReviewEvaluationFixture(
            "fixture-sample",
            "1.0",
            new FixtureProvenance("synthetic"),
            new RepositorySnapshot(
                "feature/offline-review",
                "main",
                [new RepositoryFileEntry("src/Example.cs", "public class Example {}")],
                "sample-repository"),
            new PullRequestSnapshot(
                new CodeReviewRef(
                    new RepositoryRef(
                        new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/example"),
                        "sample-repository",
                        "sample-project",
                        "sample-project"),
                    CodeReviewPlatformKind.PullRequest,
                    "42",
                    42),
                new ReviewRevision("head-sha", "base-sha", null, null, null),
                "Sample review",
                "Offline review fixture",
                "feature/offline-review",
                "main",
                [new FixtureChangedFile("src/Example.cs", ChangeType.Add, "+++ b/src/Example.cs", "public class Example {}")]),
            Scenarios:
            [
                new FixtureScenario(
                    "instructions-good",
                    RepositoryOverlay: new FixtureRepositoryOverlay(
                    [
                        new RepositoryFileEntry(
                            ".meister-propr/instructions-csharp.md",
                            "Focus on correctness regressions, especially sitemap coverage."),
                    ])),
            ],
            Expectations: new FixtureExpectations([], [], []),
            ProRVPrefilterExpectations: new FixtureProRVPrefilterExpectations([]));
    }

    private static PullRequest CreatePullRequest()
    {
        return new PullRequest(
            "https://dev.azure.com/example",
            "sample-project",
            "sample-repository",
            "sample-repository",
            42,
            1,
            "Sample review",
            null,
            "feature/offline-review",
            "main",
            [new ChangedFile("src/Example.cs", ChangeType.Add, "public class Example {}", "+++ b/src/Example.cs")]);
    }
}
