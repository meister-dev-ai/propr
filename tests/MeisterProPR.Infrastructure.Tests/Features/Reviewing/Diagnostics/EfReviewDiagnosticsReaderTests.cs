// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Diagnostics;

public sealed class EfReviewDiagnosticsReaderTests
{
    [Fact]
    public async Task GetJobProtocolAsync_WhenJobUsesAgenticFileByFile_ProjectsStrategyContextAndFileOutcome()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 11, 2);
        job.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);

        var fileResult = new ReviewFileResult(job.Id, "src/Foo.cs");
        fileResult.MarkCompleted("Per-file summary", []);
        job.FileReviewResults.Add(fileResult);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Foo.cs",
            FileResultId = fileResult.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.ReviewStrategySelected,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-3),
                InputTextSample = "{\"strategy\":\"agentic_file_by_file\"}",
                OutputSummary = "{\"strategy\":\"AgenticFileByFile\"}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.AgenticFilePlanCreated,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                InputTextSample = "{\"stage\":\"planning\"}",
                OutputSummary = "{\"anchorFilePath\":\"src/Foo.cs\"}",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        Assert.NotNull(result);
        var returnedProtocol = Assert.Single(result!.Protocols);
        Assert.Equal(ReviewStrategy.AgenticFileByFile, returnedProtocol.ResolvedReviewStrategy);
        Assert.Equal(ReviewStrategySelectionSource.ClientDefault, returnedProtocol.StrategySelectionSource);
        Assert.Equal(ScmProvider.AzureDevOps, returnedProtocol.Provider);
        Assert.Equal("https://dev.azure.com/org", returnedProtocol.ProviderScopePath);
        Assert.Equal("proj", returnedProtocol.ProviderProjectKey);
        Assert.Equal("repo", returnedProtocol.RepositoryId);
        Assert.Equal(11, returnedProtocol.PullRequestId);

        Assert.NotNull(returnedProtocol.FileOutcome);
        Assert.Equal("src/Foo.cs", returnedProtocol.FileOutcome!.FilePath);
        Assert.True(returnedProtocol.FileOutcome.IsComplete);
        Assert.False(returnedProtocol.FileOutcome.IsFailed);
        Assert.False(returnedProtocol.FileOutcome.IsExcluded);
        Assert.False(returnedProtocol.FileOutcome.IsCarriedForward);
        Assert.False(returnedProtocol.FileOutcome.IsDegraded);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenJobContainsCommentRelevanceEvents_ReturnsNamesAndPayloadsUnchanged()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Foo.cs",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.CommentRelevanceFilterDegraded,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                InputTextSample = "{\"degradedComponents\":[\"comment_relevance_evaluator\"],\"fallbackChecks\":[\"pre_filter_comments_retained\"]}",
                OutputSummary = "{\"discarded\":[{\"message\":\"Overall cleanup suggestion.\",\"reasonCodes\":[\"summary_level_only\"]}]}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.CommentRelevanceFilterSelectionFallback,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                InputTextSample = "{\"degradedCause\":\"Selected filter missing.\"}",
                OutputSummary = "{\"decisionSources\":{\"fallback_mode\":1}}",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(job.ClientId, result!.ClientId);
        var returnedProtocol = Assert.Single(result.Protocols);
        Assert.Equal("src/Foo.cs", returnedProtocol.Label);

        var degradedEvent = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.CommentRelevanceFilterDegraded);
        Assert.Contains("comment_relevance_evaluator", degradedEvent.InputTextSample ?? string.Empty);
        Assert.Contains("summary_level_only", degradedEvent.OutputSummary ?? string.Empty);

        var fallbackEvent = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.CommentRelevanceFilterSelectionFallback);
        Assert.Contains("Selected filter missing.", fallbackEvent.InputTextSample ?? string.Empty);
        Assert.Contains("fallback_mode", fallbackEvent.OutputSummary ?? string.Empty);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenJobContainsFinalGateEvents_ReturnsNamesAndPayloadsUnchanged()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "synthesis",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.ReviewFindingGateSummary,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                InputTextSample = "{\"candidateCount\":3,\"publishCount\":1,\"summaryOnlyCount\":1,\"dropCount\":1}",
                OutputSummary =
                    "{\"candidateCount\":3,\"publishCount\":1,\"summaryOnlyCount\":1,\"dropCount\":1,\"categoryCounts\":{\"cross_cutting\":1},\"invariantBlockedCount\":1}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.ReviewFindingGateDecision,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                InputTextSample = "{\"candidateCount\":3,\"publishCount\":1,\"summaryOnlyCount\":1,\"dropCount\":1}",
                OutputSummary =
                    "{\"findingId\":\"finding-001\",\"disposition\":\"Drop\",\"category\":\"per_file_comment\",\"provenance\":{\"originKind\":\"per_file_comment\",\"generatedByStage\":\"per_file_review\",\"sourceFilePath\":\"src/Foo.cs\"},\"evidence\":null,\"reasonCodes\":[\"invariant_contradiction\"],\"blockedInvariantIds\":[\"review_comment_message_required\"],\"ruleSource\":\"invariant_contradiction_rules\",\"summaryText\":null}",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        Assert.NotNull(result);
        var returnedProtocol = Assert.Single(result!.Protocols);

        var summaryEvent = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.ReviewFindingGateSummary);
        Assert.Contains("summaryOnlyCount", summaryEvent.InputTextSample ?? string.Empty);
        Assert.Contains("invariantBlockedCount", summaryEvent.OutputSummary ?? string.Empty);

        var decisionEvent = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.ReviewFindingGateDecision);
        Assert.Contains("finding-001", decisionEvent.OutputSummary ?? string.Empty);
        Assert.Contains("review_comment_message_required", decisionEvent.OutputSummary ?? string.Empty);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenProtocolContainsSessionEvents_PreservesSessionTurnAndFallbackPayloads()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 8, 1);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Foo.cs",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.ReviewAgentSessionBinding,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-3),
                InputTextSample =
                    "{" +
                    "\"sessionOwnerId\":\"session-1\",\"conversationOwnerId\":\"session-1\",\"bindingMethod\":\"created_remote_thread\",\"bindingOutcome\":\"succeeded\",\"promptMode\":\"InitialBind\",\"remoteConversationId\":\"conv-1\",\"sessionMode\":\"ProviderManagedSession\"" +
                    "}",
                OutputSummary =
                    "{" +
                    "\"providerResponseId\":\"resp-1\"" +
                    "}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.ReviewAgentSessionTurn,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                InputTextSample =
                    "{" +
                    "\"turnNumber\":2,\"sessionMode\":\"ProviderManagedSession\",\"contextStrategy\":\"DeltaContext\",\"promptMode\":\"CurrentPromptOnly\",\"usedRemoteConversation\":true,\"usedLocalReplay\":false,\"remoteConversationId\":\"conv-1\",\"providerSessionId\":\"conv-1\",\"providerResponseId\":\"resp-2\"" +
                    "}",
                OutputSummary =
                    "{" +
                    "\"outputSample\":\"Working set refined.\",\"continuationHandle\":{\"handleType\":\"ProviderSession\",\"handleId\":\"conv-1\"}" +
                    "}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.ReviewAgentSessionFallback,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                InputTextSample =
                    "{" +
                    "\"fromMode\":\"ProviderManagedSession\",\"toMode\":\"LocalManagedSession\",\"reason\":\"provider_session_continue_failed\",\"turnNumber\":2" +
                    "}",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        Assert.NotNull(result);
        var returnedProtocol = Assert.Single(result!.Protocols);
        Assert.NotNull(returnedProtocol.AgentSession);
        Assert.True(returnedProtocol.AgentSession!.UsedManagedRemoteConversation);
        Assert.Equal("conv-1", returnedProtocol.AgentSession.RemoteConversationId);
        Assert.Equal("created_remote_thread", returnedProtocol.AgentSession.BindingMethod);
        Assert.Equal("succeeded", returnedProtocol.AgentSession.BindingOutcome);
        Assert.Equal("InitialBind", returnedProtocol.AgentSession.PromptMode);
        Assert.True(returnedProtocol.AgentSession.UsedLocalReplay);
        Assert.Equal("provider_session_continue_failed", returnedProtocol.AgentSession.FallbackReason);

        var binding = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.ReviewAgentSessionBinding);
        Assert.Contains("created_remote_thread", binding.InputTextSample ?? string.Empty);
        Assert.Contains("resp-1", binding.OutputSummary ?? string.Empty);

        var sessionTurn = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.ReviewAgentSessionTurn);
        Assert.Contains("ProviderManagedSession", sessionTurn.InputTextSample ?? string.Empty);
        Assert.Contains("DeltaContext", sessionTurn.InputTextSample ?? string.Empty);
        Assert.Contains("CurrentPromptOnly", sessionTurn.InputTextSample ?? string.Empty);
        Assert.Contains("ProviderSession", sessionTurn.OutputSummary ?? string.Empty);

        var fallback = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.ReviewAgentSessionFallback);
        Assert.Contains("provider_session_continue_failed", fallback.InputTextSample ?? string.Empty);
        Assert.Contains("LocalManagedSession", fallback.InputTextSample ?? string.Empty);
        Assert.Null(fallback.OutputSummary);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenProtocolContainsOnlyBindingEvent_ProjectsManagedConversationWithoutFallback()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 9, 1);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Foo.cs",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.ReviewAgentSessionBinding,
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample =
                    "{" +
                    "\"sessionOwnerId\":\"session-1\",\"conversationOwnerId\":\"session-1\",\"bindingMethod\":\"created_remote_thread\",\"bindingOutcome\":\"succeeded\",\"promptMode\":\"InitialBind\",\"remoteConversationId\":\"conv-9\",\"sessionMode\":\"ProviderManagedSession\"" +
                    "}",
                OutputSummary = "{\"providerResponseId\":\"resp-9\"}",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        var returnedProtocol = Assert.Single(result!.Protocols);
        Assert.NotNull(returnedProtocol.AgentSession);
        Assert.True(returnedProtocol.AgentSession!.UsedManagedRemoteConversation);
        Assert.Equal("conv-9", returnedProtocol.AgentSession.RemoteConversationId);
        Assert.Equal("created_remote_thread", returnedProtocol.AgentSession.BindingMethod);
        Assert.Equal("succeeded", returnedProtocol.AgentSession.BindingOutcome);
        Assert.Equal("InitialBind", returnedProtocol.AgentSession.PromptMode);
        Assert.False(returnedProtocol.AgentSession.UsedLocalReplay);
        Assert.Null(returnedProtocol.AgentSession.FallbackReason);
    }

    [Theory]
    [InlineData("blocked_scope_violation")]
    [InlineData("blocked_budget_exhausted")]
    [InlineData("failed")]
    public async Task GetJobProtocolAsync_WhenAgenticStageBTraceIncludesRuntimeStatuses_PreservesStatusPayload(string runtimeStatus)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 12, 1);
        job.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);

        var fileResult = new ReviewFileResult(job.Id, "src/Foo.cs");
        fileResult.MarkCompleted("Per-file summary", []);
        job.FileReviewResults.Add(fileResult);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Foo.cs",
            FileResultId = fileResult.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.AgenticFileDegraded,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                InputTextSample = "{\"stage\":\"investigation\",\"taskId\":\"task-001\"}",
                OutputSummary =
                    $"{{\"Status\":\"degraded\",\"ToolUsage\":[{{\"ToolName\":\"get_file_content\",\"Status\":\"{runtimeStatus}\",\"Target\":\"src/Foo.cs\"}}],\"Degraded\":true,\"candidateCount\":0}}",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        var returnedProtocol = Assert.Single(result!.Protocols);
        var degradedEvent = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.AgenticFileDegraded);
        Assert.Contains(runtimeStatus, degradedEvent.OutputSummary ?? string.Empty);
        Assert.True(returnedProtocol.FileOutcome!.IsDegraded);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenFileProtocolContainsFollowUpEvents_ProjectsFollowUpUsageTriggerCompletionAndDependency()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 15, 1);
        job.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);

        var fileResult = new ReviewFileResult(job.Id, "src/Foo.cs");
        fileResult.MarkCompleted("Per-file summary", []);
        job.FileReviewResults.Add(fileResult);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Foo.cs",
            FileResultId = fileResult.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.AgenticFilePlanCreated,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-3),
                InputTextSample = "{\"strategy\":\"agentic_file_by_file\",\"stage\":\"planning\",\"file\":\"src/Foo.cs\"}",
                OutputSummary =
                    "{\"anchorFilePath\":\"src/Foo.cs\",\"investigationTasks\":[{\"taskId\":\"task-001\",\"triggerFamily\":\"dispatch_or_registration\"}]}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.AgenticFileInvestigationResult,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                InputTextSample =
                    "{\"strategy\":\"agentic_file_by_file\",\"stage\":\"investigation\",\"taskId\":\"task-001\",\"anchorFile\":\"src/Foo.cs\"}",
                OutputSummary =
                    "{\"status\":\"completed\",\"degraded\":false,\"diagnosticsOnly\":false,\"evidenceSetId\":\"evidence-task-001\",\"candidateCount\":1}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.AgenticFileFollowUpDependencyRecorded,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                InputTextSample =
                    "{\"anchorFile\":\"src/Foo.cs\",\"taskId\":\"task-001\",\"triggerFamily\":\"dispatch_or_registration\"}",
                OutputSummary =
                    "{\"dependencyRecorded\":true,\"evidenceSetId\":\"evidence-task-001\",\"findingId\":\"finding-001\",\"relatedFindingIds\":[\"finding-001\"]}",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        Assert.NotNull(result);
        var returnedProtocol = Assert.Single(result!.Protocols);
        Assert.NotNull(returnedProtocol.FollowUp);
        Assert.True(returnedProtocol.FollowUp!.Used);
        Assert.Equal("dispatch_or_registration", returnedProtocol.FollowUp.TriggerFamily);
        Assert.True(returnedProtocol.FollowUp.CompletedSuccessfully);
        Assert.True(returnedProtocol.FollowUp.DependencyRecorded);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenProtocolContainsRepeatedJudgmentDecision_ProjectsDecisionDetails()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 16, 1);
        job.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "synthesis",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.RepeatedJudgmentDecision,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                InputTextSample =
                    "{\"findingId\":\"candidate-001\",\"evidenceSetId\":\"evidence-task-001\",\"sourceOriginId\":\"task-001\"}",
                OutputSummary =
                    "{\"agreementState\":\"Agreed\",\"recommendedDisposition\":\"Publish\",\"usedSameEvidenceSet\":true,\"reasonCodes\":[\"verified_bounded_claim_support\"]}",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        Assert.NotNull(result);
        var returnedProtocol = Assert.Single(result!.Protocols);
        Assert.NotNull(returnedProtocol.RepeatedJudgment);
        Assert.Equal("candidate-001", returnedProtocol.RepeatedJudgment!.FindingId);
        Assert.Equal("evidence-task-001", returnedProtocol.RepeatedJudgment.EvidenceSetId);
        Assert.Equal("Agreed", returnedProtocol.RepeatedJudgment.AgreementState);
        Assert.Equal("Publish", returnedProtocol.RepeatedJudgment.RecommendedDisposition);
        Assert.True(returnedProtocol.RepeatedJudgment.UsedSameEvidenceSet);
        Assert.Contains(ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport, returnedProtocol.RepeatedJudgment.ReasonCodes);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenProtocolContainsProRvEvents_ProjectsProRvVisibility()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 17, 1);
        job.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null,
            ReviewPipelineProfileProvider.AgenticBaselineProfileId);

        var fileResult = new ReviewFileResult(job.Id, "src/Foo.cs");
        fileResult.MarkCompleted("Per-file summary", []);
        job.FileReviewResults.Add(fileResult);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Foo.cs",
            FileResultId = fileResult.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.ReviewPipelineProfileApplied,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-4),
                InputTextSample = "{\"filePath\":\"src/Foo.cs\",\"profileId\":\"agentic-baseline\",\"strategy\":\"AgenticFileByFile\"}",
                OutputSummary =
                    $"{{\"profileId\":\"agentic-baseline\",\"dispatchStageIds\":[\"{AgenticProRvPrefilterStage.StageIdConstant}\"],\"perFileStageIds\":[\"{AgenticConfidenceFloorStage.StageIdConstant}\"],\"finalizationStageIds\":[\"{ReviewPipelineProfileProvider.FinalizeStageFamilyId}\"]}}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.ProRVPrefilterStarted,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-3),
                InputTextSample =
                    $"{{\"purpose\":\"ProRVPrefilter\",\"filePath\":\"src/Foo.cs\",\"stageId\":\"{AgenticProRvPrefilterStage.StageIdConstant}\",\"details\":null}}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.AiCall,
                Name = ReviewProtocolEventNames.ProRVPrefilterAiCall,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                InputTextSample =
                    $"{{\"purpose\":\"ProRVPrefilter\",\"filePath\":\"src/Foo.cs\",\"stageId\":\"{AgenticProRvPrefilterStage.StageIdConstant}\",\"status\":\"Success\",\"language\":\"csharp\",\"runtimeSource\":\"dedicated_runtime\"}}",
                SystemPrompt =
                    $"{{\"purpose\":\"ProRVPrefilter\",\"stageId\":\"{AgenticProRvPrefilterStage.StageIdConstant}\",\"modelId\":\"gpt-5.4-mini\",\"runtimeSource\":\"dedicated_runtime\"}}",
                OutputSummary = "{\"ranked_checks\":[]}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.ProRVPrefilterCompleted,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                InputTextSample =
                    $"{{\"purpose\":\"ProRVPrefilter\",\"filePath\":\"src/Foo.cs\",\"stageId\":\"{AgenticProRvPrefilterStage.StageIdConstant}\",\"details\":null}}",
                OutputSummary =
                    "{\"runtimeSource\":\"dedicated_runtime\",\"modelId\":\"gpt-5.4-mini\",\"proRvStatus\":\"Success\",\"guidanceCount\":1,\"language\":\"csharp\"}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.ProRVFocusedGuidanceApplied,
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = "{\"filePath\":\"src/Foo.cs\",\"promptKind\":\"agentic_file_planning\",\"applied\":true,\"guidanceCount\":1}",
                OutputSummary = "{\"guidanceIds\":[\"js/incomplete-sanitization\"]}",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        var returnedProtocol = Assert.Single(result!.Protocols);
        Assert.NotNull(returnedProtocol.ProRvPrefilter);
        Assert.True(returnedProtocol.ProRvPrefilter!.Selected);
        Assert.True(returnedProtocol.ProRvPrefilter.AiCallRecorded);
        Assert.Equal("completed", returnedProtocol.ProRvPrefilter.ExecutionState);
        Assert.Equal(AgenticProRvPrefilterStage.StageIdConstant, returnedProtocol.ProRvPrefilter.StageId);
        Assert.Equal("dedicated_runtime", returnedProtocol.ProRvPrefilter.RuntimeSource);
        Assert.Equal("gpt-5.4-mini", returnedProtocol.ProRvPrefilter.ModelId);
        Assert.Equal("csharp", returnedProtocol.ProRvPrefilter.Language);
        Assert.Equal("Success", returnedProtocol.ProRvPrefilter.PrefilterStatus);
        Assert.Equal(1, returnedProtocol.ProRvPrefilter.GuidanceCount);
        Assert.True(returnedProtocol.ProRvPrefilter.GuidanceApplied);
        Assert.Equal("agentic_file_planning", returnedProtocol.ProRvPrefilter.AppliedPromptKind);
        Assert.Contains("js/incomplete-sanitization", returnedProtocol.ProRvPrefilter.AppliedGuidanceIds);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenCurrentJobContainsResumedFileResult_IncludesInheritedSourceProtocol()
    {
        var sourceJob = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 27, 3);
        sourceJob.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);

        var sourceFileResult = new ReviewFileResult(sourceJob.Id, "src/Inherited.cs");
        sourceFileResult.MarkCompleted(
            "Inherited summary",
            [new ReviewComment("src/Inherited.cs", 42, CommentSeverity.Warning, "Inherited comment")]);
        sourceJob.FileReviewResults.Add(sourceFileResult);

        var olderFailedProtocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = sourceJob.Id,
            AttemptNumber = 1,
            Label = "src/Inherited.cs",
            FileResultId = sourceFileResult.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-9),
            Outcome = "Failed",
        };
        olderFailedProtocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = olderFailedProtocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = "failed_event",
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-9),
                Error = "boom",
            });

        var sourceProtocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = sourceJob.Id,
            AttemptNumber = 2,
            Label = "src/Inherited.cs",
            FileResultId = sourceFileResult.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-8),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-7),
            Outcome = "Completed",
            TotalInputTokens = 120,
            TotalOutputTokens = 45,
            IterationCount = 3,
            ToolCallCount = 2,
        };
        sourceProtocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = sourceProtocol.Id,
                Kind = ProtocolEventKind.AiCall,
                Name = "ai_call_iter_1",
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-7),
                InputTokens = 120,
                OutputTokens = 45,
                InputTextSample = "user prompt",
                SystemPrompt = "system prompt",
                OutputSummary = "source output",
            });

        sourceJob.Protocols.Add(olderFailedProtocol);
        sourceJob.Protocols.Add(sourceProtocol);

        var currentJob = new ReviewJob(Guid.NewGuid(), sourceJob.ClientId, "https://dev.azure.com/org", "proj", "repo", 27, 3);
        currentJob.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);

        var freshProtocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = currentJob.Id,
            AttemptNumber = 3,
            Label = "synthesis",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Outcome = "Completed",
        };
        currentJob.Protocols.Add(freshProtocol);

        var resumedFileResult = ReviewFileResult.CreateResumed(currentJob.Id, sourceFileResult);
        currentJob.FileReviewResults.Add(resumedFileResult);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(currentJob.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(currentJob));
        repository.GetByIdWithProtocolsAsync(sourceJob.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(sourceJob));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(currentJob.Id, ct: CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Protocols.Count);

        var inherited = Assert.Single(result.Protocols, protocol => protocol.IsInherited);
        Assert.Equal(currentJob.Id, inherited.JobId);
        Assert.Equal(resumedFileResult.Id, inherited.FileResultId);
        Assert.Equal("src/Inherited.cs", inherited.Label);
        Assert.Equal("Inherited summary", inherited.FinalSummary);
        Assert.Single(inherited.FinalComments!);
        Assert.Equal("Inherited comment", inherited.FinalComments![0].Message);
        Assert.NotNull(inherited.FileOutcome);
        Assert.True(inherited.FileOutcome!.IsComplete);
        Assert.False(inherited.FileOutcome.IsCarriedForward);
        Assert.Equal("src/Inherited.cs", inherited.FileOutcome.FilePath);
        Assert.NotNull(inherited.Inheritance);
        Assert.Equal(sourceJob.Id, inherited.Inheritance!.SourceJobId);
        Assert.Equal(sourceFileResult.Id, inherited.Inheritance.SourceFileResultId);
        Assert.Equal(sourceProtocol.Id, inherited.Inheritance.SourceProtocolId);
        Assert.Equal(sourceProtocol.CompletedAt, inherited.Inheritance.SourceCompletedAt);
        Assert.Single(inherited.Events);
        Assert.Equal("ai_call_iter_1", inherited.Events[0].Name);
        Assert.Null(inherited.Events[0].InputTextSample);
        Assert.Null(inherited.Events[0].SystemPrompt);
        Assert.Equal("ai-call", inherited.Events[0].EventCategory);
        Assert.Equal(
            "Inherited event payload omitted from this view to keep large same-revision retry traces responsive. Open the source job protocol to inspect the original captured body.",
            inherited.Events[0].OutputSummary);

        var current = Assert.Single(result.Protocols, protocol => !protocol.IsInherited);
        Assert.Equal(currentJob.Id, current.JobId);
        Assert.Equal("synthesis", current.Label);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenDbContextFactoryIsAvailable_LoadsOnlyLinkedInheritedSourceProtocols()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"EfReviewDiagnosticsReaderTests-{Guid.NewGuid():N}")
            .Options;

        var sourceJobId = Guid.NewGuid();
        var sourceFileResultId = Guid.NewGuid();
        var sourceProtocolId = Guid.NewGuid();
        var currentJobId = Guid.NewGuid();
        var currentClientId = Guid.NewGuid();
        var priorFileResult = new ReviewFileResult(sourceJobId, "src/Inherited.cs");
        priorFileResult.MarkCompleted(
            "Inherited summary",
            [new ReviewComment("src/Inherited.cs", 7, CommentSeverity.Warning, "Inherited comment")]);

        await using (var seedDb = new MeisterProPRDbContext(options))
        {
            seedDb.ReviewJobProtocols.Add(
                new ReviewJobProtocol
                {
                    Id = sourceProtocolId,
                    JobId = sourceJobId,
                    AttemptNumber = 2,
                    Label = "src/Inherited.cs",
                    FileResultId = priorFileResult.Id,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                    CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                    Outcome = "Completed",
                    TotalInputTokens = 33,
                    TotalOutputTokens = 11,
                    IterationCount = 2,
                    ToolCallCount = 1,
                });
            seedDb.ProtocolEvents.Add(
                new ProtocolEvent
                {
                    Id = Guid.NewGuid(),
                    ProtocolId = sourceProtocolId,
                    Kind = ProtocolEventKind.AiCall,
                    Name = "ai_call_iter_1",
                    OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                    InputTokens = 33,
                    OutputTokens = 11,
                    InputTextSample = "very large input",
                    SystemPrompt = "very large system",
                    OutputSummary = "very large output",
                });

            await seedDb.SaveChangesAsync();
        }

        var currentJob = new ReviewJob(currentJobId, currentClientId, "https://dev.azure.com/org", "proj", "repo", 55, 1);
        currentJob.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);

        var freshProtocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = currentJob.Id,
            AttemptNumber = 1,
            Label = "synthesis",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };
        currentJob.Protocols.Add(freshProtocol);

        var resumedFileResult = ReviewFileResult.CreateResumed(currentJob.Id, priorFileResult);
        currentJob.FileReviewResults.Add(resumedFileResult);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(currentJob.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(currentJob));

        var sut = new EfReviewDiagnosticsReader(repository, new TestDbContextFactory(options));

        var result = await sut.GetJobProtocolAsync(currentJob.Id, ct: CancellationToken.None);

        Assert.NotNull(result);
        var inherited = Assert.Single(result!.Protocols, protocol => protocol.IsInherited);
        Assert.Equal(sourceProtocolId, inherited.Inheritance!.SourceProtocolId);
        Assert.Single(inherited.Events);
        Assert.Equal("ai_call_iter_1", inherited.Events[0].Name);
        Assert.Null(inherited.Events[0].InputTextSample);
        Assert.Null(inherited.Events[0].SystemPrompt);
        Assert.Equal(
            "Inherited event payload omitted from this view to keep large same-revision retry traces responsive. Open the source job protocol to inspect the original captured body.",
            inherited.Events[0].OutputSummary);

        await repository.DidNotReceive().GetByIdWithProtocolsAsync(sourceJobId, Arg.Any<CancellationToken>());
    }
}
