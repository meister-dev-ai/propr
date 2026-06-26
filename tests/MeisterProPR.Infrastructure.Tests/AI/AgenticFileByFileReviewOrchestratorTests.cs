// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;
using MeisterProPR.ProRV.Abstractions;
using MeisterProPR.ProRV.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class AgenticFileByFileReviewOrchestratorTests
{
    [Fact]
    public async Task ReviewAsync_RecordsStrategySelectionEvent()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);

        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
        repository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "anchor_file_path": "src/Web/Program.cs",
                                              "concerns": ["Check anchor file."],
                                              "changed_areas": ["src/Web/Program.cs"],
                                              "investigation_tasks": [],
                                              "no_investigation_reason": "No sibling-file investigation required for this anchor file."
                                            }
                                            """)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback summary")));

        var context = new ReviewSystemContext(null, [], null)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
        };

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        var result = await sut.ReviewAsync(
            job,
            CreatePr(),
            context,
            CancellationToken.None,
            Substitute.For<IChatClient>());

        Assert.Contains("file summary", result.Summary, StringComparison.Ordinal);
        await protocolRecorder.Received(1).RecordReviewStrategyEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.ReviewStrategySelected,
            Arg.Is<string?>(details => details != null && details.Contains("agentic_file_by_file", StringComparison.Ordinal)),
            Arg.Is<string?>(output => output != null && output.Contains("AgenticFileByFile", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WhenOneFileReviewFails_PreservesSuccessfulFileOutcomesInPartialResult()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
        repository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var preservedComment = new ReviewComment("a.cs", 3, CommentSeverity.Warning, "Preserved finding from successful file.");
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(
                Arg.Is<PullRequest>(request => request.ChangedFiles.Count == 1 && request.ChangedFiles[0].Path == "a.cs"),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("a summary", [preservedComment]));
        aiCore.ReviewAsync(
                Arg.Is<PullRequest>(request => request.ChangedFiles.Count == 1 && request.ChangedFiles[0].Path == "b.cs"),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<ReviewResult>(new InvalidOperationException("AI failed for b.cs")));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("a.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("b.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "partial synthesis")));

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        var ex = await Assert.ThrowsAsync<PartialReviewFailureException>(() =>
            sut.ReviewAsync(
                job,
                CreatePr(
                    new ChangedFile("a.cs", ChangeType.Edit, "class A {}", "+warning"),
                    new ChangedFile("b.cs", ChangeType.Edit, "class B {}", "+failure")),
                new ReviewSystemContext(null, [], null),
                CancellationToken.None,
                chatClient));

        Assert.NotNull(ex.PartialResult);
        var partialResult = ex.PartialResult!;
        Assert.Equal("partial synthesis", partialResult.Summary);
        Assert.Contains(partialResult.Comments, comment => comment.Message == preservedComment.Message);
        Assert.Contains(storedResults, result => result.FilePath == "a.cs" && result.IsComplete && !result.IsFailed);
        Assert.Contains(storedResults, result => result.FilePath == "b.cs" && result.IsFailed && !result.IsComplete);
    }

    [Fact]
    public async Task ReviewAsync_AgenticProfileWithoutProRvDispatchStage_DoesNotInvokeProRvPrefilter()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordProRvEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
        job.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null,
            ReviewPipelineProfileProvider.AgenticExperimentalProfileId);

        repository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("src/Web/Program.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback summary")));

        var profileProvider = Substitute.For<IReviewPipelineProfileProvider>();
        profileProvider.GetProfiles(ReviewStrategy.AgenticFileByFile).Returns(
        [
            new ReviewPipelineProfile(
                ReviewPipelineProfileProvider.AgenticBaselineProfileId,
                "Agentic baseline",
                ReviewStrategy.AgenticFileByFile,
                [AgenticProRvPrefilterStage.StageIdConstant],
                [
                    AgenticConfidenceFloorStage.StageIdConstant,
                    AgenticSpeculativeCommentFilterStage.StageIdConstant,
                    AgenticInfoCommentStripStage.StageIdConstant,
                    AgenticVagueSuggestionFilterStage.StageIdConstant,
                ],
                [ReviewPipelineProfileProvider.FinalizeStageFamilyId],
                true),
            new ReviewPipelineProfile(
                ReviewPipelineProfileProvider.AgenticExperimentalProfileId,
                "Agentic experimental",
                ReviewStrategy.AgenticFileByFile,
                [],
                [
                    AgenticSpeculativeCommentFilterStage.StageIdConstant,
                    AgenticInfoCommentStripStage.StageIdConstant,
                    AgenticVagueSuggestionFilterStage.StageIdConstant,
                ],
                [ReviewPipelineProfileProvider.FinalizeStageFamilyId],
                false),
        ]);

        var proRvPrefilter = Substitute.For<IProRVPrefilter>();
        var perFilePipeline = new ReviewPipelineRunner<PerFileReviewContext>(
        [
            new AgenticProRvPrefilterStage(
                protocolRecorder,
                proRvPrefilter,
                null,
                null,
                null,
                Substitute.For<ILogger<AgenticProRvPrefilterStage>>()),
            new AgenticSpeculativeCommentFilterStage(),
            new AgenticInfoCommentStripStage(),
            new AgenticVagueSuggestionFilterStage(),
        ]);

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>(),
            perFilePipeline: perFilePipeline,
            pipelineProfileProvider: profileProvider,
            proRvPrefilter: proRvPrefilter);

        await sut.ReviewAsync(job, CreatePr(), new ReviewSystemContext(null, [], null), CancellationToken.None, chatClient);

        await proRvPrefilter.DidNotReceive()
            .RankRelevantItemsAsync(
                Arg.Any<ProRVPrefilterRequest>(),
                Arg.Any<IChatClient>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());
        await protocolRecorder.Received().RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(),
            ReviewProtocolEventNames.ReviewPipelineProfileApplied,
            Arg.Any<string>(),
            Arg.Is<string?>(output => output != null && output.Contains(ReviewPipelineProfileProvider.AgenticExperimentalProfileId, StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithProRvDisabledInContext_DoesNotInvokeProRvPrefilter()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
        repository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("src/Web/Program.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback summary")));

        var proRvPrefilter = Substitute.For<IProRVPrefilter>();

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>(),
            proRvPrefilter: proRvPrefilter);

        var context = new ReviewSystemContext(null, [], null)
        {
            EnableProRV = false,
        };

        await sut.ReviewAsync(job, CreatePr(), context, CancellationToken.None, chatClient);

        await proRvPrefilter.DidNotReceive()
            .RankRelevantItemsAsync(
                Arg.Any<ProRVPrefilterRequest>(),
                Arg.Any<IChatClient>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());

        await aiCore.Received()
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(reviewContext =>
                    !reviewContext.EnableProRV &&
                    reviewContext.PerFileHint != null &&
                    reviewContext.PerFileHint.FocusedReviewGuidance.Count == 0),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithLateAugmentationMode_UsesProRvOnlyForAugmentationPass()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
        repository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("src/Web/Program.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback summary")));

        var proRvPrefilter = Substitute.For<IProRVPrefilter>();

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>(),
            proRvPrefilter: proRvPrefilter);

        var context = new ReviewSystemContext(null, [], null)
        {
            AugmentationMode = ReviewAugmentationMode.LateAugmentation,
        };

        await sut.ReviewAsync(job, CreatePr(), context, CancellationToken.None, chatClient);

        await proRvPrefilter.Received(2)
            .RankRelevantItemsAsync(
                Arg.Any<ProRVPrefilterRequest>(),
                Arg.Any<IChatClient>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());

        await aiCore.Received()
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(reviewContext =>
                    !reviewContext.EnableProRV &&
                    reviewContext.PassKind == ReviewPassKind.Baseline &&
                    reviewContext.AugmentationMode == ReviewAugmentationMode.LateAugmentation &&
                    reviewContext.PerFileHint != null &&
                    reviewContext.PerFileHint.FocusedReviewGuidance.Count == 0),
                Arg.Any<CancellationToken>());

        await aiCore.Received()
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(reviewContext =>
                    reviewContext.EnableProRV &&
                    reviewContext.PassKind == ReviewPassKind.ProRVAugmentation &&
                    reviewContext.AugmentationMode == ReviewAugmentationMode.LateAugmentation),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithLateAugmentationMode_RunsCurrentFilePassAsBaseline()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
        repository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("src/Web/Program.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback summary")));

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        var context = new ReviewSystemContext(null, [], null)
        {
            AugmentationMode = ReviewAugmentationMode.LateAugmentation,
            EnableProRV = true,
        };

        await sut.ReviewAsync(job, CreatePr(), context, CancellationToken.None, chatClient);

        await aiCore.Received()
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(reviewContext =>
                    !reviewContext.EnableProRV &&
                    reviewContext.AugmentationMode == ReviewAugmentationMode.LateAugmentation),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public void AgenticCandidateFindingFactory_Build_PreservesBaselineOnlyProvenanceForPerFileFindings()
    {
        var fileResult = new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs");
        fileResult.MarkCompleted("summary", [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Baseline issue")]);

        var finding = Assert.Single(new AgenticCandidateFindingFactory(null).Build([fileResult]));

        Assert.Equal(ReviewPassKind.Baseline, finding.Provenance.ReviewPassKind);
        Assert.Equal(FindingProvenanceKind.BaselineOnly, finding.Provenance.FindingProvenanceKind);
    }

    [Fact]
    public async Task ReviewAsync_PrWithDuplicateChangedFilePaths_DoesNotThrowAndReviewsFileOnce()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
        repository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult("dup summary", [new ReviewComment("src/Dup.cs", 1, CommentSeverity.Warning, "Dup issue")]),
                new ReviewResult("other summary", [new ReviewComment("src/Other.cs", 1, CommentSeverity.Warning, "Other issue")]));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("src/Dup.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("src/Other.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        var pr = CreatePr(
            new ChangedFile("src/Dup.cs", ChangeType.Edit, "dup content", "+dup change"),
            new ChangedFile("src/Dup.cs", ChangeType.Edit, "dup content (duplicate entry)", "+dup change"),
            new ChangedFile("src/Other.cs", ChangeType.Edit, "other content", "+other change"));

        var result = await sut.ReviewAsync(
            job,
            pr,
            new ReviewSystemContext(null, [], null),
            CancellationToken.None,
            chatClient);

        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Is<PullRequest>(request =>
                    request.ChangedFiles.Count == 1 && request.ChangedFiles[0].Path == "src/Dup.cs"),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>());
        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Is<PullRequest>(request =>
                    request.ChangedFiles.Count == 1 && request.ChangedFiles[0].Path == "src/Other.cs"),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>());
        await aiCore.Received(2)
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());

        Assert.Contains(result.Comments, c => c.FilePath == "src/Dup.cs" && c.Message == "Dup issue");
        Assert.Contains(result.Comments, c => c.FilePath == "src/Other.cs" && c.Message == "Other issue");
    }

    [Fact]
    public async Task ReviewAsync_WithLateAugmentationMode_RunsSecondProRvAugmentationPass()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
        repository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult("baseline summary", []),
                new ReviewResult("augmentation summary", []),
                new ReviewResult("baseline summary", []),
                new ReviewResult("augmentation summary", []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("src/Web/Program.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("src/Application/Registration.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("src/Web/Program.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("src/Application/Registration.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback summary")));

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        var context = new ReviewSystemContext(null, [], null)
        {
            AugmentationMode = ReviewAugmentationMode.LateAugmentation,
            EnableProRV = true,
        };

        await sut.ReviewAsync(job, CreatePr(), context, CancellationToken.None, chatClient);

        await aiCore.Received()
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(reviewContext =>
                    reviewContext.PassKind == ReviewPassKind.ProRVAugmentation &&
                    reviewContext.EnableProRV &&
                    reviewContext.AugmentationMode == ReviewAugmentationMode.LateAugmentation),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithLateAugmentationMode_RecordsBaselineAugmentationAndMergeProtocolEvents()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
        repository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult("baseline summary", [new ReviewComment("src/Web/Program.cs", 12, CommentSeverity.Warning, "Baseline issue")]),
                new ReviewResult("augmentation summary", [new ReviewComment("src/Web/Program.cs", 20, CommentSeverity.Warning, "Augmentation issue")]));

        var pr = CreatePr(new ChangedFile("src/Web/Program.cs", ChangeType.Edit, "program content", "+builder.Services.AddFoo();"));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("src/Web/Program.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, CreateNoInvestigationPlan("src/Web/Program.cs"))),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback summary")));

        var context = new ReviewSystemContext(null, [], null)
        {
            AugmentationMode = ReviewAugmentationMode.LateAugmentation,
            EnableProRV = true,
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
        };

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        await sut.ReviewAsync(job, pr, context, CancellationToken.None, chatClient);

        Received.InOrder(() =>
        {
            protocolRecorder.RecordReviewStrategyEventAsync(
                context.ActiveProtocolId.Value,
                ReviewProtocolEventNames.ReviewStrategySelected,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

            protocolRecorder.RecordReviewStrategyEventAsync(
                context.ActiveProtocolId.Value,
                ReviewProtocolEventNames.LateSteeringBaselinePassCompleted,
                Arg.Is<string?>(details => details != null && details.Contains("\"passKind\":\"Baseline\"", StringComparison.Ordinal)),
                Arg.Any<string?>(),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

            protocolRecorder.RecordReviewStrategyEventAsync(
                context.ActiveProtocolId.Value,
                ReviewProtocolEventNames.LateSteeringAugmentationPassCompleted,
                Arg.Is<string?>(details => details != null && details.Contains("\"passKind\":\"ProRVAugmentation\"", StringComparison.Ordinal)),
                Arg.Any<string?>(),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

            protocolRecorder.RecordReviewStrategyEventAsync(
                context.ActiveProtocolId.Value,
                ReviewProtocolEventNames.LateSteeringMergeCompleted,
                Arg.Is<string?>(details =>
                    details != null &&
                    details.Contains("\"baselineCandidateCount\":1", StringComparison.Ordinal) &&
                    details.Contains("\"proRvCandidateCount\":1", StringComparison.Ordinal) &&
                    details.Contains("\"mergedCandidateCount\":2", StringComparison.Ordinal)),
                Arg.Is<string?>(output =>
                    output != null &&
                    output.Contains("\"baselineOnlyCount\":1", StringComparison.Ordinal) &&
                    output.Contains("\"proRvOnlyCount\":1", StringComparison.Ordinal) &&
                    output.Contains("\"bothCount\":0", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
        });
    }

    private static string CreateNoInvestigationPlan(string filePath)
    {
        return $$"""
                 {
                   "plan_id": "plan-{{filePath}}",
                   "anchor_file_path": "{{filePath}}",
                   "concerns": ["Check {{filePath}}."],
                   "changed_areas": ["{{filePath}}"],
                   "investigation_tasks": [],
                   "no_investigation_reason": "No explicit trigger family required deeper follow-up for this straightforward file."
                 }
                 """;
    }

    private static ReviewJob BuildJobWithResults(ReviewJob original, IEnumerable<ReviewFileResult> results)
    {
        var job = new ReviewJob(
            original.Id,
            original.ClientId,
            original.OrganizationUrl,
            original.ProjectId,
            original.RepositoryId,
            original.PullRequestId,
            original.IterationId);

        foreach (var result in results)
        {
            job.FileReviewResults.Add(result);
        }

        return job;
    }

    private static ReviewJob CreateJob()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 7);
        job.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);
        return job;
    }

    private static PullRequest CreatePr(params ChangedFile[] files)
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            42,
            7,
            "Refactor service registration",
            "Touches startup, composition root, and tests.",
            "feature/registration",
            "main",
            files.Length == 0
                ?
                [
                    new ChangedFile("src/Web/Program.cs", ChangeType.Edit, "program content", "+builder.Services.AddFoo();"),
                    new ChangedFile("src/Application/Registration.cs", ChangeType.Edit, "registration content", "+services.AddFoo();"),
                ]
                : files.ToList().AsReadOnly());
    }
}
