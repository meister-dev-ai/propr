// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging.Abstractions;
using MeisterDev.ProPR.Application.Features.CodeInsights;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Persistence;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.CodeInsights;

public sealed class CodeInsightFindingIngestionServiceTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid JobId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task HandleReviewFindingsProducedAsync_TouchesThePullRequestAndMapsEveryFindingField()
    {
        var store = Substitute.For<ICodeInsightFindingStore>();
        var sut = new CodeInsightFindingIngestionService(store, OpenGate(), NullLogger<CodeInsightFindingIngestionService>.Instance);
        var observedAt = DateTimeOffset.UtcNow;

        await sut.HandleReviewFindingsProducedAsync(CreateEvent(observedAt, CreateFindings()));

        await store.Received(1).TouchPullRequestAsync(
            Arg.Is<CodeInsightPullRequestKey>(key =>
                key.ClientId == ClientId && key.RepositoryId == "repo" && key.PullRequestId == 7),
            "Active",
            observedAt,
            // The repository's display name reaches the store, so a ranked list can name it instead of printing
            // the provider's identifier at a reader.
            "Payments API",
            Arg.Any<CancellationToken>());

        await store.Received(1).MaterialiseFindingsAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            JobId,
            "rev-1",
            observedAt,
            Arg.Is<IReadOnlyList<CodeInsightFindingSnapshot>>(snapshots =>
                snapshots.Count == 1
                && snapshots[0].Ordinal == 3
                && snapshots[0].FilePath == "src/Service.cs"
                && snapshots[0].LineNumber == 42
                && snapshots[0].Severity == CommentSeverity.Error
                && snapshots[0].Message == "Null dereference"
                && snapshots[0].OriginPassKind == "MultiPassUnion"
                && snapshots[0].OriginPassIndex == 2
                && snapshots[0].OriginPassLens == "security"
                && snapshots[0].OriginPassShadow
                && snapshots[0].ScopeRelation == ReviewCommentScopeRelation.AdjacentToChange
                && snapshots[0].SourceReadGrounding == ReviewCommentReadGrounding.Covered
                && snapshots[0].ProviderThreadId == "thread-1"
                && snapshots[0].ProviderCommentId == "comment-1"
                && snapshots[0].OriginModelId == "gpt-5.4-mini"
                && snapshots[0].OriginLogicalModelName == "thrifty-reviewer"
                && snapshots[0].OriginSymbolName == "Process"
                && snapshots[0].OriginSymbolKind == "Method"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleReviewFindingsProducedAsync_WithNoFindings_StillTouchesThePullRequest()
    {
        // A review that found nothing is a fact worth keeping: the aggregate's activity timestamp is what
        // the retention sweep and the Phase-2 "reviewed but clean" signal both rest on.
        var store = Substitute.For<ICodeInsightFindingStore>();
        var sut = new CodeInsightFindingIngestionService(store, OpenGate(), NullLogger<CodeInsightFindingIngestionService>.Instance);

        await sut.HandleReviewFindingsProducedAsync(CreateEvent(DateTimeOffset.UtcNow, []));

        await store.Received(1).TouchPullRequestAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await store.DidNotReceive().MaterialiseFindingsAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<IReadOnlyList<CodeInsightFindingSnapshot>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleReviewFindingsProducedAsync_WithTheGateClosed_TouchesNothingAtAll()
    {
        // Not even the pull-request aggregate: an unlicensed or opted-out client must leave no trace, and
        // the gate is therefore consulted before the first store call rather than after.
        var store = Substitute.For<ICodeInsightFindingStore>();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        var sut = new CodeInsightFindingIngestionService(store, gate, NullLogger<CodeInsightFindingIngestionService>.Instance);

        await sut.HandleReviewFindingsProducedAsync(CreateEvent(DateTimeOffset.UtcNow, CreateFindings()));

        await gate.Received(1).IsCollectionEnabledAsync(ClientId, Arg.Any<CancellationToken>());
        await store.DidNotReceive().TouchPullRequestAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await store.DidNotReceive().MaterialiseFindingsAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<IReadOnlyList<CodeInsightFindingSnapshot>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleReviewFindingsProducedAsync_AsksTheGateAboutTheEventsOwnClient()
    {
        var store = Substitute.For<ICodeInsightFindingStore>();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        var sut = new CodeInsightFindingIngestionService(store, gate, NullLogger<CodeInsightFindingIngestionService>.Instance);

        await sut.HandleReviewFindingsProducedAsync(CreateEvent(DateTimeOffset.UtcNow, CreateFindings()));

        await gate.Received(1).IsCollectionEnabledAsync(ClientId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleReviewFindingsProducedAsync_RefreshesTheJobsRollup()
    {
        var store = Substitute.For<ICodeInsightFindingStore>();
        var projector = Substitute.For<ICodeInsightRollupProjector>();
        var sut = new CodeInsightFindingIngestionService(store, OpenGate(), NullLogger<CodeInsightFindingIngestionService>.Instance, projector);

        await sut.HandleReviewFindingsProducedAsync(CreateEvent(DateTimeOffset.UtcNow, CreateFindings()));

        await projector.Received(1).ProjectJobAsync(JobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleReviewFindingsProducedAsync_WithTheGateClosed_DoesNotTouchTheRollup()
    {
        var store = Substitute.For<ICodeInsightFindingStore>();
        var projector = Substitute.For<ICodeInsightRollupProjector>();
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        var sut = new CodeInsightFindingIngestionService(store, gate, NullLogger<CodeInsightFindingIngestionService>.Instance, projector);

        await sut.HandleReviewFindingsProducedAsync(CreateEvent(DateTimeOffset.UtcNow, CreateFindings()));

        await projector.DidNotReceive().ProjectJobAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleReviewFindingsProducedAsync_WhenTheStoreFails_DoesNotPropagate()
    {
        // Collection is a passive observer, and containment belongs here rather than in whoever calls it: a
        // caller that produced findings has already done the work worth keeping, and a second caller would
        // otherwise have to re-establish this on its own.
        var store = Substitute.For<ICodeInsightFindingStore>();
        store.TouchPullRequestAsync(
                Arg.Any<CodeInsightPullRequestKey>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the collection database is unreachable"));
        var sut = new CodeInsightFindingIngestionService(store, OpenGate(), NullLogger<CodeInsightFindingIngestionService>.Instance);

        await sut.HandleReviewFindingsProducedAsync(CreateEvent(DateTimeOffset.UtcNow, CreateFindings()));

        await store.DidNotReceive().MaterialiseFindingsAsync(
            Arg.Any<CodeInsightPullRequestKey>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<IReadOnlyList<CodeInsightFindingSnapshot>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleReviewFindingsProducedAsync_WhenCancelled_StillPropagates()
    {
        // Containment covers failures, not cancellation: a cancelled host has to be allowed to stop.
        var store = Substitute.For<ICodeInsightFindingStore>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        store.TouchPullRequestAsync(
                Arg.Any<CodeInsightPullRequestKey>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        var sut = new CodeInsightFindingIngestionService(store, OpenGate(), NullLogger<CodeInsightFindingIngestionService>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.HandleReviewFindingsProducedAsync(
                CreateEvent(DateTimeOffset.UtcNow, CreateFindings()),
                cancelled.Token));
    }

    /// <summary>A gate that permits collection, so a test can exercise the mapping rather than the gate.</summary>
    private static ICodeInsightsCollectionGate OpenGate()
    {
        var gate = Substitute.For<ICodeInsightsCollectionGate>();
        gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        return gate;
    }

    private static ReviewFindingsProducedEvent CreateEvent(
        DateTimeOffset observedAt,
        IReadOnlyList<ReviewFindingProduced> findings)
    {
        return new ReviewFindingsProducedEvent(
            ClientId,
            "repo",
            7,
            JobId,
            "rev-1",
            "Active",
            observedAt,
            findings,
            "Payments API");
    }

    private static List<ReviewFindingProduced> CreateFindings()
    {
        return
        [
            new ReviewFindingProduced(
                3,
                "src/Service.cs",
                42,
                CommentSeverity.Error,
                "Null dereference",
                "MultiPassUnion",
                2,
                "security",
                true,
                ReviewCommentScopeRelation.AdjacentToChange,
                ReviewCommentReadGrounding.Covered,
                "thread-1",
                "comment-1",
                "gpt-5.4-mini",
                "thrifty-reviewer",
                "Process",
                "Method"),
        ];
    }
}
