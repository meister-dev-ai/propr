// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
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

        var result = await sut.GetJobProtocolAsync(job.Id, CancellationToken.None);

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

        var result = await sut.GetJobProtocolAsync(job.Id, CancellationToken.None);

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

        var result = await sut.GetJobProtocolAsync(job.Id, CancellationToken.None);

        Assert.NotNull(result);
        var returnedProtocol = Assert.Single(result!.Protocols);

        var summaryEvent = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.ReviewFindingGateSummary);
        Assert.Contains("summaryOnlyCount", summaryEvent.InputTextSample ?? string.Empty);
        Assert.Contains("invariantBlockedCount", summaryEvent.OutputSummary ?? string.Empty);

        var decisionEvent = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.ReviewFindingGateDecision);
        Assert.Contains("finding-001", decisionEvent.OutputSummary ?? string.Empty);
        Assert.Contains("review_comment_message_required", decisionEvent.OutputSummary ?? string.Empty);
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

        var result = await sut.GetJobProtocolAsync(job.Id, CancellationToken.None);

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

        var result = await sut.GetJobProtocolAsync(job.Id, CancellationToken.None);

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

        var result = await sut.GetJobProtocolAsync(job.Id, CancellationToken.None);

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
}
