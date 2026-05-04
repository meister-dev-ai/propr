// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Application.Tests.Services;

/// <summary>
///     Unit tests for <see cref="ThreadMemoryService" /> covering US1–US4.
/// </summary>
public sealed class ThreadMemoryServiceTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ProtocolId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    [Fact]
    public async Task HandleThreadResolvedAsync_NormalCase_StoresEmbeddingAndAppendsActivityLogEntry()
    {
        var (embedder, repo, _, activityLog, service) = CreateService();
        var vector = new[] { 0.1f, 0.2f };
        embedder.GenerateResolutionSummaryAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns("Resolution summary.");
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(vector);

        var evt = new ThreadResolvedDomainEvent(
            ClientId,
            "repo-1",
            42,
            7,
            "src/Foo.cs",
            "diff",
            "comment history",
            DateTimeOffset.UtcNow);
        await service.HandleThreadResolvedAsync(evt);

        await repo.Received(1)
            .UpsertAsync(
                Arg.Is<ThreadMemoryRecord>(r =>
                    r.ClientId == ClientId && r.ThreadId == 7 && r.RepositoryId == "repo-1"),
                Arg.Any<CancellationToken>());
        await activityLog.Received(1)
            .AppendAsync(
                Arg.Is<MemoryActivityLogEntry>(e =>
                    e.ClientId == ClientId && e.ThreadId == 7 && e.Action == MemoryActivityAction.Stored &&
                    e.Reason == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleThreadResolvedAsync_EmbedderThrows_AppendsStoredEntryWithErrorReasonAndDoesNotThrow()
    {
        var (embedder, repo, _, activityLog, service) = CreateService();
        embedder.GenerateResolutionSummaryAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns("Summary.");
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("no embedding connection"));

        var evt = new ThreadResolvedDomainEvent(
            ClientId,
            "repo-1",
            42,
            7,
            null,
            null,
            "history",
            DateTimeOffset.UtcNow);
        var ex = await Record.ExceptionAsync(() => service.HandleThreadResolvedAsync(evt));

        Assert.Null(ex);
        await repo.DidNotReceive().UpsertAsync(Arg.Any<ThreadMemoryRecord>(), Arg.Any<CancellationToken>());
        await activityLog.Received(1)
            .AppendAsync(
                Arg.Is<MemoryActivityLogEntry>(e =>
                    e.Action == MemoryActivityAction.Stored && e.Reason != null &&
                    e.Reason.Contains("no embedding connection")),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleThreadReopenedAsync_RecordExists_AppendsRemovedEntryWithDeletedReason()
    {
        var (_, repo, _, activityLog, service) = CreateService();
        repo.RemoveByThreadAsync(ClientId, "repo-1", 7, Arg.Any<CancellationToken>())
            .Returns(true);

        var evt = new ThreadReopenedDomainEvent(ClientId, "repo-1", 42, 7, DateTimeOffset.UtcNow);
        await service.HandleThreadReopenedAsync(evt);

        await activityLog.Received(1)
            .AppendAsync(
                Arg.Is<MemoryActivityLogEntry>(e =>
                    e.ClientId == ClientId && e.ThreadId == 7 && e.Action == MemoryActivityAction.Removed &&
                    e.Reason == "deleted"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleThreadReopenedAsync_NoRecord_AppendsRemovedEntryWithNoOpReason()
    {
        var (_, repo, _, activityLog, service) = CreateService();
        repo.RemoveByThreadAsync(ClientId, "repo-1", 7, Arg.Any<CancellationToken>())
            .Returns(false);

        var evt = new ThreadReopenedDomainEvent(ClientId, "repo-1", 42, 7, DateTimeOffset.UtcNow);
        await service.HandleThreadReopenedAsync(evt);

        await activityLog.Received(1)
            .AppendAsync(
                Arg.Is<MemoryActivityLogEntry>(e =>
                    e.Action == MemoryActivityAction.Removed && e.Reason == "no_op"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleThreadReopenedAsync_RepositoryThrows_DoesNotThrow()
    {
        var (_, repo, _, _, service) = CreateService();
        repo.RemoveByThreadAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("db error"));

        var evt = new ThreadReopenedDomainEvent(ClientId, "repo-1", 42, 7, DateTimeOffset.UtcNow);
        var ex = await Record.ExceptionAsync(() => service.HandleThreadReopenedAsync(evt));

        Assert.Null(ex);
    }

    [Fact]
    public async Task RecordNoOpAsync_NormalCase_AppendsNoOpActivityLogEntry()
    {
        var (_, _, _, activityLog, service) = CreateService();

        await service.RecordNoOpAsync(ClientId, "repo-1", 42, 7, "Active", "Active", "still_active");

        await activityLog.Received(1)
            .AppendAsync(
                Arg.Is<MemoryActivityLogEntry>(e =>
                    e.ClientId == ClientId && e.ThreadId == 7 && e.Action == MemoryActivityAction.NoOp &&
                    e.Reason == "still_active"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordNoOpAsync_ActivityLogThrows_DoesNotThrow()
    {
        var (_, _, _, activityLog, service) = CreateService();
        activityLog.AppendAsync(Arg.Any<MemoryActivityLogEntry>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("log unavailable"));

        var ex = await Record.ExceptionAsync(() =>
            service.RecordNoOpAsync(ClientId, "repo-1", 42, 7, null, "Active", "still_active"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task RetrieveAndReconsiderAsync_MatchFound_ReturnsReconsideredResult()
    {
        var (embedder, repo, recorder, _, service) = CreateService();
        var draftResult = new ReviewResult("draft summary", []);
        var matches = new List<ThreadMemoryMatchDto>
        {
            new(Guid.NewGuid(), 5, "src/Foo.cs", "Fixed by adding null check.", 0.92f),
        };
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repo.FindSimilarAsync(
                Arg.Any<Guid>(),
                Arg.Any<float[]>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(matches);

        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);

        var result = await service.RetrieveAndReconsiderAsync(
            ClientId,
            job,
            "src/Foo.cs",
            "diff",
            draftResult,
            ProtocolId);

        Assert.NotNull(result);
        await recorder.Received(1)
            .RecordMemoryEventAsync(
                ProtocolId,
                Arg.Is<string>(s => s == "memory_retrieval_executed"),
                Arg.Is<string?>(d => d != null && d.Contains("1")),
                Arg.Is<string?>(e => e == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAndReconsiderAsync_NoMatches_ReturnsDraftResultUnchanged()
    {
        var (embedder, repo, _, _, service) = CreateService();
        var draftResult = new ReviewResult("draft summary", []);
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repo.FindSimilarAsync(
                Arg.Any<Guid>(),
                Arg.Any<float[]>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<ThreadMemoryMatchDto>());

        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var result = await service.RetrieveAndReconsiderAsync(
            ClientId,
            job,
            "src/Foo.cs",
            null,
            draftResult,
            ProtocolId);

        Assert.Same(draftResult, result);
    }

    [Fact]
    public async Task RetrieveAndReconsiderAsync_NoSemanticMatches_UsesExactFileFallback()
    {
        var (embedder, repo, recorder, _, service) = CreateService(out var chatClient);
        var draftResult = new ReviewResult("draft summary", []);
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repo.FindSimilarAsync(
                Arg.Any<Guid>(),
                Arg.Any<float[]>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<ThreadMemoryMatchDto>());
        repo.FindByFilePathAsync(ClientId, "repo", "/package.json", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
                new List<ThreadMemoryMatchDto>
                {
                    new(
                        Guid.NewGuid(),
                        6023,
                        "/package.json",
                        "Closed without code change.",
                        0f,
                        "exact_file_fallback"),
                });

        var responseJson =
            """{"summary":"reconsidered","comments":[],"confidence_evaluations":[],"investigation_complete":true,"loop_complete":true}""";
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseJson))));

        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);

        var result = await service.RetrieveAndReconsiderAsync(
            ClientId,
            job,
            "/package.json",
            "diff",
            draftResult,
            ProtocolId);

        Assert.Equal("reconsidered", result.Summary);
        await repo.Received(1)
            .FindByFilePathAsync(ClientId, "repo", "/package.json", Arg.Any<int>(), Arg.Any<CancellationToken>());
        await recorder.Received(1)
            .RecordMemoryEventAsync(
                ProtocolId,
                Arg.Is<string>(s => s == "memory_retrieval_executed"),
                Arg.Is<string?>(d =>
                    d != null &&
                    d.Contains("\"retrievalMode\":\"exact_file_fallback\"") &&
                    d.Contains("\"resultCount\":1") &&
                    d.Contains("6023")),
                Arg.Is<string?>(e => e == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAndReconsiderAsync_EmbedderFails_ReturnsDraftResultWithoutThrowing()
    {
        var (embedder, _, _, _, service) = CreateService();
        var draftResult = new ReviewResult("draft", []);
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("no embedding"));

        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var result = await service.RetrieveAndReconsiderAsync(ClientId, job, "src/Foo.cs", null, draftResult, null);

        Assert.Same(draftResult, result);
    }

    [Fact]
    public async Task RetrieveAndReconsiderAsync_RepositoryFails_RecordsFailedEventAndReturnsDraft()
    {
        var (embedder, repo, recorder, _, service) = CreateService();
        var draftResult = new ReviewResult("draft", []);
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repo.FindSimilarAsync(
                Arg.Any<Guid>(),
                Arg.Any<float[]>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("store unavailable"));

        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var result = await service.RetrieveAndReconsiderAsync(
            ClientId,
            job,
            "src/Foo.cs",
            null,
            draftResult,
            ProtocolId);

        Assert.Same(draftResult, result);
        await recorder.Received(1)
            .RecordMemoryEventAsync(
                ProtocolId,
                Arg.Is<string>(s => s == "memory_operation_failed"),
                Arg.Any<string?>(),
                Arg.Is<string?>(e => e != null && e.Contains("store unavailable")),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAndReconsiderAsync_MatchFound_RecordsAiCallForReconsideration()
    {
        var (embedder, repo, recorder, _, service) = CreateService(out var chatClient);
        var draftResult = new ReviewResult("draft summary", []);
        var matches = new List<ThreadMemoryMatchDto>
        {
            new(Guid.NewGuid(), 5, "src/Foo.cs", "Fixed by adding null check.", 0.92f),
        };
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repo.FindSimilarAsync(
                Arg.Any<Guid>(),
                Arg.Any<float[]>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(matches);
        var responseJson =
            """{"summary":"reconsidered","comments":[],"confidence_evaluations":[],"investigation_complete":true,"loop_complete":true}""";
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseJson))
        {
            Usage = new UsageDetails { InputTokenCount = 150, OutputTokenCount = 75 },
        };
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(chatResponse));

        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);
        await service.RetrieveAndReconsiderAsync(ClientId, job, "src/Foo.cs", "diff", draftResult, ProtocolId);

        await recorder.Received(1)
            .RecordAiCallAsync(
                ProtocolId,
                0,
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Is<string?>(n => n == "ai_call_memory_reconsideration"));
    }

    [Fact]
    public async Task RetrieveAndReconsiderAsync_MatchFound_AddsTokensToProtocol()
    {
        var (embedder, repo, recorder, _, service) = CreateService(out var chatClient);
        var draftResult = new ReviewResult("draft summary", []);
        var matches = new List<ThreadMemoryMatchDto>
        {
            new(Guid.NewGuid(), 5, "src/Foo.cs", "Fixed by adding null check.", 0.92f),
        };
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repo.FindSimilarAsync(
                Arg.Any<Guid>(),
                Arg.Any<float[]>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(matches);
        var responseJson =
            """{"summary":"reconsidered","comments":[],"confidence_evaluations":[],"investigation_complete":true,"loop_complete":true}""";
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseJson))
        {
            Usage = new UsageDetails { InputTokenCount = 200, OutputTokenCount = 100 },
        };
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(chatResponse));

        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);
        await service.RetrieveAndReconsiderAsync(ClientId, job, "src/Foo.cs", "diff", draftResult, ProtocolId);

        await recorder.Received(1)
            .AddTokensAsync(
                ProtocolId,
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAndReconsiderAsync_CommentsDiscarded_ReconsiderationDetailsContainDiscarded()
    {
        var (embedder, repo, recorder, _, service) = CreateService(out var chatClient);
        var commentA = new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, "Missing null check");
        var commentB = new ReviewComment("src/Foo.cs", 20, CommentSeverity.Error, "Unused variable xyz");
        var draftResult = new ReviewResult("draft summary", [commentA, commentB]);
        var matches = new List<ThreadMemoryMatchDto>
        {
            new(Guid.NewGuid(), 5, "src/Foo.cs", "Was accepted by design.", 0.88f),
        };
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repo.FindSimilarAsync(
                Arg.Any<Guid>(),
                Arg.Any<float[]>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(matches);
        // AI reconsideration discards commentB — only commentA is in the response
        var responseJson =
            """{"summary":"reconsidered","comments":[{"file_path":"src/Foo.cs","line_number":10,"severity":"warning","message":"Missing null check"}],"confidence_evaluations":[],"investigation_complete":true,"loop_complete":true}""";
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseJson));
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(chatResponse));

        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);
        await service.RetrieveAndReconsiderAsync(ClientId, job, "src/Foo.cs", "diff", draftResult, ProtocolId);

        await recorder.Received(1)
            .RecordMemoryEventAsync(
                ProtocolId,
                Arg.Is<string>(s => s == "memory_reconsideration_completed"),
                Arg.Is<string?>(d =>
                    d != null &&
                    d.Contains("discarded") &&
                    d.Contains("Unused variable xyz") &&
                    d.Contains("\"discardedCount\":1") &&
                    d.Contains("\"originalCommentCount\":2") &&
                    d.Contains("\"finalCommentCount\":1")),
                Arg.Is<string?>(e => e == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAndReconsiderAsync_AiReturnsEmptyResponse_EmitsFailedEventAndReturnsDraft()
    {
        var (embedder, repo, recorder, _, service) = CreateService(out var chatClient);
        var comment = new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, "Some warning");
        var draftResult = new ReviewResult("draft summary", [comment]);
        var matches = new List<ThreadMemoryMatchDto>
        {
            new(Guid.NewGuid(), 5, "src/Foo.cs", "Fixed by adding null check.", 0.90f),
        };
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.5f });
        repo.FindSimilarAsync(
                Arg.Any<Guid>(),
                Arg.Any<float[]>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(matches);
        // AI returns empty response — ReconsiderWithAiAsync returns null
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty))));

        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var result = await service.RetrieveAndReconsiderAsync(
            ClientId,
            job,
            "src/Foo.cs",
            "diff",
            draftResult,
            ProtocolId);

        // Draft is returned unchanged
        Assert.Same(draftResult, result);

        // memory_reconsideration_failed must be emitted so the protocol is not left dangling
        await recorder.Received(1)
            .RecordMemoryEventAsync(
                ProtocolId,
                Arg.Is<string>(s => s == "memory_reconsideration_failed"),
                Arg.Is<string?>(d =>
                    d != null &&
                    d.Contains("\"reason\":\"ai_returned_null_or_parse_failed\"") &&
                    d.Contains("\"originalCommentCount\":1")),
                Arg.Is<string?>(e => e != null && e.Contains("retained unchanged")),
                Arg.Any<CancellationToken>());

        // memory_reconsideration_completed must NOT be emitted
        await recorder.DidNotReceive()
            .RecordMemoryEventAsync(
                ProtocolId,
                Arg.Is<string>(s => s == "memory_reconsideration_completed"),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetrieveAndReconsiderAsync_AiReturnsUnparseableJson_EmitsFailedEventAndReturnsDraft()
    {
        var (embedder, repo, recorder, _, service) = CreateService(out var chatClient);
        var comment = new ReviewComment("src/Bar.cs", 5, CommentSeverity.Error, "Null dereference");
        var draftResult = new ReviewResult("draft", [comment]);
        var matches = new List<ThreadMemoryMatchDto>
        {
            new(Guid.NewGuid(), 9, "src/Bar.cs", "Accepted as known pattern.", 0.85f),
        };
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.3f });
        repo.FindSimilarAsync(
                Arg.Any<Guid>(),
                Arg.Any<float[]>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(matches);
        // AI returns invalid JSON — ParseReconsiderationResponse returns null
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not valid json at all"))));

        var job = new ReviewJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var result = await service.RetrieveAndReconsiderAsync(
            ClientId,
            job,
            "src/Bar.cs",
            "diff",
            draftResult,
            ProtocolId);

        Assert.Same(draftResult, result);
        await recorder.Received(1)
            .RecordMemoryEventAsync(
                ProtocolId,
                Arg.Is<string>(s => s == "memory_reconsideration_failed"),
                Arg.Is<string?>(d => d != null && d.Contains("\"reason\":\"ai_returned_null_or_parse_failed\"")),
                Arg.Is<string?>(e => e != null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindDuplicateSuppressionMatchAsync_SemanticMatchInSamePullRequest_ReturnsHistoricalDuplicate()
    {
        var (embedder, repo, _, _, service) = CreateService();
        var memoryRecordId = Guid.NewGuid();

        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .Returns([0.2f, 0.8f]);
        repo.FindSimilarInPullRequestAsync(
                ClientId,
                "repo-1",
                42,
                Arg.Any<float[]>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new ThreadMemoryMatchDto(
                    memoryRecordId,
                    700,
                    "/src/Foo.cs",
                    "Validate the configuration before using it.",
                    0.91f),
            ]);

        var match = await service.FindDuplicateSuppressionMatchAsync(
            ClientId,
            "repo-1",
            42,
            "/src/Foo.cs",
            "Validate the configuration before using it.");

        Assert.True(match.IsDuplicate);
        Assert.Equal("historical_similarity_match", match.ReasonCode);
        Assert.Equal(700, match.ThreadId);
        Assert.Equal(memoryRecordId, match.MemoryRecordId);
        Assert.False(match.IsDegraded);
    }

    [Fact]
    public async Task
        FindDuplicateSuppressionMatchAsync_EmbeddingFails_UsesPullRequestFileFallbackAndReportsDegradedMode()
    {
        var (embedder, repo, _, _, service) = CreateService();
        var memoryRecordId = Guid.NewGuid();

        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("embedding unavailable"));
        repo.FindByPullRequestFilePathAsync(
                ClientId,
                "repo-1",
                42,
                "/src/Foo.cs",
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(
            [
                new ThreadMemoryMatchDto(
                    memoryRecordId,
                    701,
                    "/src/Foo.cs",
                    "Validate the config before using it as a connection string.",
                    0f,
                    "exact_file_fallback"),
            ]);

        var match = await service.FindDuplicateSuppressionMatchAsync(
            ClientId,
            "repo-1",
            42,
            "/src/Foo.cs",
            "Validate the configuration before using it as the connection string.");

        Assert.True(match.IsDuplicate);
        Assert.Equal("historical_similarity_match", match.ReasonCode);
        Assert.True(match.IsDegraded);
        Assert.Contains("thread_memory_embedding", match.DegradedComponents);
        Assert.Contains("pull_request_file_path_memory", match.FallbackChecks);
        Assert.Equal(memoryRecordId, match.MemoryRecordId);
    }

    [Fact]
    public async Task FindDuplicateSuppressionMatchAsync_EmbeddingFailsOnce_SkipsRepeatedEmbeddingCallsForSameClient()
    {
        var (embedder, repo, _, _, service) = CreateService();

        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("embedding unavailable"));
        repo.FindByPullRequestFilePathAsync(
                ClientId,
                "repo-1",
                42,
                "/src/Foo.cs",
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<ThreadMemoryMatchDto>());

        await service.FindDuplicateSuppressionMatchAsync(
            ClientId,
            "repo-1",
            42,
            "/src/Foo.cs",
            "First finding text.");

        await service.FindDuplicateSuppressionMatchAsync(
            ClientId,
            "repo-1",
            42,
            "/src/Foo.cs",
            "Second finding text.");

        await embedder.Received(1)
            .GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>());
        await repo.Received(2)
            .FindByPullRequestFilePathAsync(
                ClientId,
                "repo-1",
                42,
                "/src/Foo.cs",
                Arg.Any<int>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindDuplicateSuppressionMatchAsync_RepositoryFails_ReturnsNoMatchWithDegradedReason()
    {
        var (embedder, repo, _, _, service) = CreateService();

        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), ClientId, Arg.Any<CancellationToken>())
            .Returns([0.5f]);
        repo.FindSimilarInPullRequestAsync(
                ClientId,
                "repo-1",
                42,
                Arg.Any<float[]>(),
                Arg.Any<int>(),
                Arg.Any<float>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("repository unavailable"));
        repo.FindByPullRequestFilePathAsync(
                ClientId,
                "repo-1",
                42,
                "/src/Foo.cs",
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("repository unavailable"));

        var match = await service.FindDuplicateSuppressionMatchAsync(
            ClientId,
            "repo-1",
            42,
            "/src/Foo.cs",
            "Validate the configuration before using it.");

        Assert.False(match.IsDuplicate);
        Assert.True(match.IsDegraded);
        Assert.Contains("thread_memory_repository", match.DegradedComponents);
        Assert.Contains("repository lookups", match.DegradedCause ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static (
        IThreadMemoryEmbedder embedder,
        IThreadMemoryRepository repo,
        IProtocolRecorder recorder,
        IMemoryActivityLog activityLog,
        ThreadMemoryService service) CreateService()
    {
        return CreateService(out _);
    }

    private static (
        IThreadMemoryEmbedder embedder,
        IThreadMemoryRepository repo,
        IProtocolRecorder recorder,
        IMemoryActivityLog activityLog,
        ThreadMemoryService service) CreateService(out IChatClient chatClient)
    {
        var embedder = Substitute.For<IThreadMemoryEmbedder>();
        var repo = Substitute.For<IThreadMemoryRepository>();
        var recorder = Substitute.For<IProtocolRecorder>();
        var activityLog = Substitute.For<IMemoryActivityLog>();
        var opts = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions());
        var logger = Substitute.For<ILogger<ThreadMemoryService>>();
        chatClient = Substitute.For<IChatClient>();

        var service = new ThreadMemoryService(embedder, repo, recorder, activityLog, opts, logger, chatClient);
        return (embedder, repo, recorder, activityLog, service);
    }
}
