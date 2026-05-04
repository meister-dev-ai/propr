// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Diagnostics;

public sealed class EfReviewDiagnosticsReaderTests
{
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
}
