// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.CommentRelevance;

public sealed class CommentRelevanceFilterExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_WithoutSelectedRegistryFilter_ReturnsNull()
    {
        var protocolRecorder = CreateProtocolRecorder();
        var registry = new CommentRelevanceFilterRegistry([], CommentRelevanceFilterSelection.None);
        var sut = new CommentRelevanceFilterExecutor(registry, protocolRecorder);

        var result = await sut.ExecuteAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result);
        await protocolRecorder.DidNotReceive().RecordCommentRelevanceEventAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenSelectedFilterMissing_UsesRegistrySelectionFallback()
    {
        var protocolId = Guid.NewGuid();
        var protocolRecorder = CreateProtocolRecorder();
        var registry = new CommentRelevanceFilterRegistry([], new CommentRelevanceFilterSelection("hybrid-v1"));
        var sut = new CommentRelevanceFilterExecutor(registry, protocolRecorder);

        var result = await sut.ExecuteAsync(CreateRequest(protocolId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("hybrid-v1", result!.ImplementationId);
        Assert.Equal(2, result.KeptCount);
        Assert.Contains("comment_relevance_registry", result.DegradedComponents);
        Assert.Contains("pre_filter_comments_retained", result.FallbackChecks);
        Assert.All(result.Decisions, decision => Assert.Equal(CommentRelevanceFilterDecision.FallbackModeSource, decision.DecisionSource));

        await protocolRecorder.Received().RecordCommentRelevanceEventAsync(
            Arg.Is(protocolId),
            Arg.Is(ReviewProtocolEventNames.CommentRelevanceFilterSelectionFallback),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(value => value == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenFilterReturnsEvaluatorDegradedResult_RecordsAiUsage()
    {
        var protocolId = Guid.NewGuid();
        var protocolRecorder = CreateProtocolRecorder();
        var request = CreateRequest(protocolId, "hybrid-v1", [CreateComment("Keep me.")]);
        var filter = new StubCommentRelevanceFilter(
            "hybrid-v1",
            "1.2.3",
            (incomingRequest, _) => Task.FromResult(
                new CommentRelevanceFilterResult(
                    incomingRequest.SelectedImplementationId!,
                    "1.2.3",
                    incomingRequest.FilePath,
                    incomingRequest.Comments.Count,
                    [
                        new CommentRelevanceFilterDecision(
                            CommentRelevanceFilterDecision.KeepDecision, incomingRequest.Comments[0], [], CommentRelevanceFilterDecision.AiAdjudicationSource),
                    ],
                    ["comment_relevance_evaluator"],
                    [],
                    null,
                    new FilterAiTokenUsage("hybrid-v1", incomingRequest.FilePath, 14, 6, AiConnectionModelCategory.Default, "filter-model"))));
        var registry = new CommentRelevanceFilterRegistry([filter], new CommentRelevanceFilterSelection("hybrid-v1"));
        var sut = new CommentRelevanceFilterExecutor(registry, protocolRecorder);

        var result = await sut.ExecuteAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, result!.KeptCount);
        Assert.NotNull(result.AiTokenUsage);

        await protocolRecorder.Received().RecordCommentRelevanceEventAsync(
            Arg.Is(protocolId),
            Arg.Is(ReviewProtocolEventNames.CommentRelevanceEvaluatorDegraded),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(value => value == null),
            Arg.Any<CancellationToken>());
        await protocolRecorder.Received().RecordAiCallAsync(
            Arg.Is(protocolId),
            0,
            14,
            6,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(value => value == null),
            Arg.Any<CancellationToken>(),
            Arg.Is<string?>(value => value == ReviewProtocolEventNames.CommentRelevanceEvaluatorAiCall),
            Arg.Is<string?>(value => value == null));
        await protocolRecorder.Received().AddTokensAsync(
            Arg.Is(protocolId),
            14,
            6,
            AiConnectionModelCategory.Default,
            Arg.Is<string?>(value => value == "filter-model"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenFilterThrows_ReturnsFailOpenFallback()
    {
        var protocolId = Guid.NewGuid();
        var protocolRecorder = CreateProtocolRecorder();
        var filter = new StubCommentRelevanceFilter(
            "hybrid-v1",
            "1.2.3",
            (_, _) => throw new InvalidOperationException("filter blew up"));
        var registry = new CommentRelevanceFilterRegistry([filter], new CommentRelevanceFilterSelection("hybrid-v1"));
        var sut = new CommentRelevanceFilterExecutor(registry, protocolRecorder);

        var result = await sut.ExecuteAsync(CreateRequest(protocolId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(2, result!.KeptCount);
        Assert.Contains("hybrid-v1", result.DegradedComponents);
        Assert.Contains("pre_filter_comments_retained", result.FallbackChecks);
        Assert.Contains("Cause: filter blew up", result.DegradedCause, StringComparison.Ordinal);

        await protocolRecorder.Received().RecordCommentRelevanceEventAsync(
            Arg.Is(protocolId),
            Arg.Is(ReviewProtocolEventNames.CommentRelevanceFilterDegraded),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(value => value == null),
            Arg.Any<CancellationToken>());
    }

    private static CommentRelevanceFilterRequest CreateRequest(
        Guid? protocolId = null,
        string? selectedImplementationId = null,
        IReadOnlyList<ReviewComment>? comments = null)
    {
        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "@@ -1 +1 @@");
        var pullRequest = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/x",
            "main",
            [file]);

        return new CommentRelevanceFilterRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            selectedImplementationId,
            file.Path,
            file,
            pullRequest,
            comments ?? [CreateComment("Keep me."), CreateComment("Discard me.", 2)],
            new ReviewSystemContext(null, [], null),
            protocolId);
    }

    private static ReviewComment CreateComment(string message, int? lineNumber = 1)
    {
        return new ReviewComment("src/Foo.cs", lineNumber, CommentSeverity.Warning, message);
    }

    private static IProtocolRecorder CreateProtocolRecorder()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.RecordCommentRelevanceEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.AddTokensAsync(
                Arg.Any<Guid>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return protocolRecorder;
    }

    private sealed class StubCommentRelevanceFilter(
        string implementationId,
        string implementationVersion,
        Func<CommentRelevanceFilterRequest, CancellationToken, Task<CommentRelevanceFilterResult>> handler) : ICommentRelevanceFilter
    {
        public string ImplementationId { get; } = implementationId;

        public string ImplementationVersion { get; } = implementationVersion;

        public Task<CommentRelevanceFilterResult> FilterAsync(CommentRelevanceFilterRequest request, CancellationToken ct = default)
        {
            return handler(request, ct);
        }
    }
}
