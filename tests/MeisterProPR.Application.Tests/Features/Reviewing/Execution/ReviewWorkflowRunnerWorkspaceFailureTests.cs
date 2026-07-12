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

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class ReviewWorkflowRunnerWorkspaceFailureTests
{
    [Fact]
    public async Task RunAsync_WhenWorkspacePreparationFails_UsesRemoteToolFallback()
    {
        var jobRepository = Substitute.For<IJobRepository>();
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var diagnosticsReader = Substitute.For<IReviewDiagnosticsReader>();
        var fixtureAccessor = Substitute.For<IReviewEvaluationFixtureAccessor>();
        var fixtureValidator = Substitute.For<IReviewEvaluationFixtureValidator>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        var workspaceManager = Substitute.For<IReviewRepositoryWorkspaceManager>();
        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        var fileByFileReviewOrchestrator = Substitute.For<IFileByFileReviewOrchestrator>();

        var fixture = CreateFixture();
        var job = new ReviewJob(
            Guid.NewGuid(), Guid.NewGuid(), fixture.PullRequestSnapshot.CodeReview.Repository.Host.HostBaseUrl,
            fixture.PullRequestSnapshot.CodeReview.Repository.ProjectPath, fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
            fixture.PullRequestSnapshot.CodeReview.Number, 1);
        var request = new ReviewWorkflowRequest(
            job,
            Substitute.For<IChatClient>(),
            "gpt-4o",
            fixture,
            new EvaluationConfiguration(
                "baseline",
                new EvaluationModelSelection(["gpt-4o"]),
                new EvaluationOutputOptions("artifacts/run.json", "full")));
        var pullRequest = CreatePullRequest();
        var reviewTools = Substitute.For<IReviewContextTools>();
        var expectedReviewResult = new ReviewResult("fallback", []);

        jobs.GetById(job.Id).Returns(job);
        pullRequestFetcher.FetchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>()).Returns(pullRequest);
        workspaceManager.PrepareAsync(Arg.Any<ReviewRepositoryWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewRepositoryWorkspacePreparationResult(
                    null, new ReviewWorkspaceFailure("fetch", "workspace_prepare_failed", "git fetch failed", true, true)));
        reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(reviewTools);
        instructionFetcher.FetchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([]);
        exclusionFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.Default);
        diagnosticsReader.GetJobProtocolAsync(job.Id, ct: Arg.Any<CancellationToken>()).Returns(new GetReviewJobProtocolResult(job.Id, []));
        fileByFileReviewOrchestrator.ReviewAsync(job, pullRequest, Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), request.ChatClient)
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
            fileByFileReviewOrchestrator,
            workspaceManager);

        var result = await sut.RunAsync(request, CancellationToken.None);

        Assert.Same(expectedReviewResult, result.FinalResult);
        reviewContextToolsFactory.Received(1)
            .Create(Arg.Is<ReviewContextToolsRequest>(candidate => candidate.Workspace == null && candidate.WorkspaceFailure != null));
    }

    private static ReviewEvaluationFixture CreateFixture()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "repo", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        return new ReviewEvaluationFixture(
            "fixture",
            "1.0",
            new FixtureProvenance("synthetic"),
            new RepositorySnapshot(
                "feature/demo",
                "main",
                [new RepositoryFileEntry("src/Example.cs", "class Example {}")],
                "repo"),
            new PullRequestSnapshot(
                review,
                new ReviewRevision("head", "base", null, null, null),
                "Title",
                null,
                "feature/demo",
                "main",
                [new FixtureChangedFile("src/Example.cs", ChangeType.Add, "+class Example {}", "class Example {}")]),
            [],
            []);
    }

    private static PullRequest CreatePullRequest()
    {
        return new PullRequest("https://github.com", "acme/propr", "repo", "repo", 42, 1, "Title", null, "feature/demo", "main", []);
    }
}
