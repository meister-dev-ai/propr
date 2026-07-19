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
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Services;

/// <summary>
///     Covers the code-grounded storage gate: a thread closed as "fixed" only becomes suppression
///     memory when an actual code change corroborates it, while a deliberate human acceptance is stored
///     regardless of any code change.
/// </summary>
public sealed class ThreadMemoryGroundingGateTests
{
    private static readonly Guid ClientId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    [Theory]
    [InlineData(ThreadAnchorCodeChange.Unchanged, "closed_without_code_change")]
    [InlineData(ThreadAnchorCodeChange.Unknown, "code_change_undetermined")]
    public async Task ClaimsFix_WithoutCorroboratingCodeChange_SkipsBeforeAnyAiCall(
        ThreadAnchorCodeChange codeChange,
        string expectedReason)
    {
        var (embedder, repo, activityLog, service) = CreateService();

        var evt = Resolved(ThreadResolutionIntent.ClaimsFix, codeChange, "Bot: fix this.\nDev: will fix later.");
        await service.HandleThreadResolvedAsync(evt);

        // Skipping happens before the summary/embedding calls, so a premature close costs no AI spend.
        await embedder.DidNotReceive().GenerateResolutionSummaryAsync(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
        await repo.DidNotReceive().UpsertAsync(Arg.Any<ThreadMemoryRecord>(), Arg.Any<CancellationToken>());
        await activityLog.Received(1).AppendAsync(
            Arg.Is<MemoryActivityLogEntry>(e =>
                e.ThreadId == 7 && e.Action == MemoryActivityAction.NoOp && e.Reason == expectedReason),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClaimsFix_WithCorroboratingCodeChange_StoresWhenResolutionIsDeterminable()
    {
        var (embedder, repo, _, service) = CreateService();
        embedder.GenerateResolutionSummaryAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThreadResolutionSummary("Fixed by adding the guard clause.", ResolutionClarity.ResolvedByChange));
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.1f, 0.2f });

        var evt = Resolved(ThreadResolutionIntent.ClaimsFix, ThreadAnchorCodeChange.Changed, "Bot: null deref.\nDev: fixed.");
        await service.HandleThreadResolvedAsync(evt);

        await repo.Received(1).UpsertAsync(
            Arg.Is<ThreadMemoryRecord>(r => r.ThreadId == 7 && r.ResolutionSummary == "Fixed by adding the guard clause."),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ThreadAnchorCodeChange.Unchanged)]
    [InlineData(ThreadAnchorCodeChange.Unknown)]
    public async Task AcceptedByHuman_StoresRegardlessOfCodeChange(ThreadAnchorCodeChange codeChange)
    {
        var (embedder, repo, _, service) = CreateService();
        embedder.GenerateResolutionSummaryAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(new ThreadResolutionSummary("Accepted as by-design.", ResolutionClarity.AcceptedWithoutChange));
        embedder.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 0.3f });

        var evt = Resolved(ThreadResolutionIntent.AcceptedByHuman, codeChange, "Bot: consider X.\nDev: intentional, by design.");
        await service.HandleThreadResolvedAsync(evt);

        await repo.Received(1).UpsertAsync(
            Arg.Is<ThreadMemoryRecord>(r => r.ThreadId == 7 && r.ResolutionSummary == "Accepted as by-design."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcceptedByHuman_WithFailedSummaryGeneration_DoesNotStore()
    {
        var (embedder, repo, _, service) = CreateService();
        embedder.GenerateResolutionSummaryAsync(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string>(),
                Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ThreadResolutionSummary(
                    ThreadResolutionSummary.GenerationFailedSummary,
                    ResolutionClarity.Undetermined));

        var evt = Resolved(ThreadResolutionIntent.AcceptedByHuman, ThreadAnchorCodeChange.Unchanged, "Dev: by design.");
        await service.HandleThreadResolvedAsync(evt);

        await repo.DidNotReceive().UpsertAsync(Arg.Any<ThreadMemoryRecord>(), Arg.Any<CancellationToken>());
    }

    private static ThreadResolvedDomainEvent Resolved(
        ThreadResolutionIntent intent,
        ThreadAnchorCodeChange codeChange,
        string commentHistory) =>
        new(ClientId, "repo-1", 42, 7, "src/Foo.cs", null, commentHistory, DateTimeOffset.UtcNow, intent, codeChange);

    private static (
        IThreadMemoryEmbedder embedder,
        IThreadMemoryRepository repo,
        IMemoryActivityLog activityLog,
        ThreadMemoryService service) CreateService()
    {
        var embedder = Substitute.For<IThreadMemoryEmbedder>();
        var repo = Substitute.For<IThreadMemoryRepository>();
        var recorder = Substitute.For<IProtocolRecorder>();
        var activityLog = Substitute.For<IMemoryActivityLog>();
        var opts = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions());
        var logger = Substitute.For<ILogger<ThreadMemoryService>>();

        var service = new ThreadMemoryService(embedder, repo, recorder, activityLog, opts, logger);
        return (embedder, repo, activityLog, service);
    }
}
