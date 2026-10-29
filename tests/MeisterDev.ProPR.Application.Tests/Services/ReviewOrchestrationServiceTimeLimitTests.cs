// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     The maximum review duration of a tenant, from the store to the scan and from the scan to the job. The scan
///     reads the time left when it starts, and a review that ended at the limit is published and completed like any
///     other review, not failed.
/// </summary>
public sealed class ReviewOrchestrationServiceTimeLimitTests
{
    [Fact]
    public async Task TheScan_ReceivesTheTenantsMaximumReviewDurationWithTheTimeLeft()
    {
        var job = NewJob();
        var timeLimits = Substitute.For<IReviewTimeLimitReader>();
        timeLimits.ReadAsync(job.Id, Arg.Any<CancellationToken>()).Returns(new ReviewTimeLimitReading(1800, TimeSpan.FromSeconds(1500)));
        var (orchestrator, captured) = OrchestratorReturning(new ReviewResult("Summary", []));

        await CreateService(orchestrator, Substitute.For<IReviewJobExecutionStore>(), timeLimits).ProcessAsync(job, CancellationToken.None);

        var timeLimit = captured().TimeLimit;
        Assert.NotNull(timeLimit);
        Assert.Equal(1800, timeLimit.MaxReviewDurationSeconds);
        Assert.False(timeLimit.IsReached());
    }

    [Fact]
    public async Task ATenantWithoutAMaximumReviewDuration_ScansWithoutOne()
    {
        var job = NewJob();
        var timeLimits = Substitute.For<IReviewTimeLimitReader>();
        timeLimits.ReadAsync(job.Id, Arg.Any<CancellationToken>()).Returns((ReviewTimeLimitReading?)null);
        var (orchestrator, captured) = OrchestratorReturning(new ReviewResult("Summary", []));

        await CreateService(orchestrator, Substitute.For<IReviewJobExecutionStore>(), timeLimits).ProcessAsync(job, CancellationToken.None);

        Assert.Null(captured().TimeLimit);
    }

    // The acceptance criterion: a job that exceeds the tenant's maximum duration ends with a summary and a reason,
    // not a failure. The result carries the time limit into publication and onto the job.
    [Fact]
    public async Task AReviewThatEndedAtTheTimeLimit_IsPublishedAndCompletedNotFailed()
    {
        var job = NewJob();
        var store = Substitute.For<IReviewJobExecutionStore>();
        var timeLimited = new ReviewResult("The review covered the parser.", [])
        {
            TimeLimitReached = true,
            TimeLimitSeconds = 600,
            TimeLimitSkippedFilePaths = ["src/Late.cs"],
        };
        var (orchestrator, _) = OrchestratorReturning(timeLimited);

        await CreateService(orchestrator, store, Substitute.For<IReviewTimeLimitReader>()).ProcessAsync(job, CancellationToken.None);

        await store.Received(1).SetResultAsync(
            job.Id,
            Arg.Is<ReviewResult>(result => result.TimeLimitReached && result.TimeLimitSkippedFilePaths.SequenceEqual(new[] { "src/Late.cs" })),
            Arg.Any<CancellationToken>());
        await store.DidNotReceiveWithAnyArgs().SetFailedAsync(default, default!, default);
    }

    private static ReviewJob NewJob()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, null, null));
        return job;
    }

    private static (IFileByFileReviewOrchestrator Orchestrator, Func<ReviewSystemContext> Captured) OrchestratorReturning(ReviewResult result)
    {
        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        ReviewSystemContext? captured = null;
        orchestrator
            .ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(context => captured = context),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(result);
        return (orchestrator, () => captured ?? throw new InvalidOperationException("The scan did not run."));
    }

    private static ReviewOrchestrationService CreateService(
        IFileByFileReviewOrchestrator orchestrator,
        IReviewJobExecutionStore store,
        IReviewTimeLimitReader timeLimits)
    {
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(Substitute.For<IReviewContextTools>());

        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        instructionFetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        instructionEvaluator
            .EvaluateRelevanceAsync(Arg.Any<IReadOnlyList<RepositoryInstruction>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        exclusionFetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewExclusionRules.Empty));

        var prFetcher = Substitute.For<IPullRequestFetcher>();
        prFetcher.FetchRefAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PullRequestRef("feature/test", "main", PrStatus.Active)));
        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewRevision?>(), Arg.Any<IReviewRepositoryWorkspace?>())
            .Returns(call => new PullRequest(
                call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<string>(2), call.ArgAt<string>(2), call.ArgAt<int>(3), call.ArgAt<int>(4),
                "Test PR", null, "feature/x", "main",
                new List<ChangedFile> { new("src/Late.cs", ChangeType.Edit, "content", "diff") }.AsReadOnly()));

        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetCommentResolutionBehaviorAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CommentResolutionBehavior.Silent));
        clientRegistry.GetEffectiveReviewerIdentityAsync(Arg.Any<Guid>(), Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
            .Returns(call => new ReviewerIdentity(call.ArgAt<ProviderHostRef>(1), "reviewer", "reviewer", "reviewer", false));
        clientRegistry.GetReviewerIdentityAsync(Arg.Any<Guid>(), Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
            .Returns(call => new ReviewerIdentity(call.ArgAt<ProviderHostRef>(1), "reviewer", "reviewer", "reviewer", false));

        var prScanRepository = Substitute.For<IReviewPrScanRepository>();
        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));

        var workspace = Substitute.For<IReviewRepositoryWorkspace>();
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);
        var workspaceManager = Substitute.For<IReviewRepositoryWorkspaceManager>();
        workspaceManager.PrepareAsync(Arg.Any<ReviewRepositoryWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewRepositoryWorkspacePreparationResult(workspace, null));

        var publicationService = Substitute.For<ICodeReviewPublicationService>();
        publicationService.Provider.Returns(ScmProvider.AzureDevOps);
        publicationService.PublishReviewAsync(
                Arg.Any<Guid>(), Arg.Any<CodeReviewRef>(), Arg.Any<ReviewRevision>(), Arg.Any<ReviewResult>(), Arg.Any<ReviewerIdentity>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPublicationContext?>())
            .Returns(Task.FromResult(ReviewCommentPostingDiagnosticsDto.Empty()));
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetCodeReviewPublicationService(Arg.Any<ScmProvider>()).Returns(publicationService);
        registry.GetRegisteredCapabilities(Arg.Any<ScmProvider>()).Returns([]);

        return new ReviewOrchestrationService(
            store,
            prFetcher,
            registry,
            clientRegistry,
            prScanRepository,
            Substitute.For<IProtocolRecorder>(),
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<ReviewOrchestrationService>>(),
            AiConnectionTestFactory.CreateChatRuntimeResolver(),
            orchestrator,
            workspaceManager: workspaceManager,
            timeLimits: timeLimits);
    }
}
