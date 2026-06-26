// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Offline;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Offline;

public sealed class InMemoryProtocolRecorderTests
{
    [Fact]
    public async Task RecordPromptStageEvidenceAsync_PromptStageEvidenceEvent_PreservesPromptStageMetadata()
    {
        var jobs = new InMemoryReviewJobRepository();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 7);
        await jobs.AddAsync(job, CancellationToken.None);

        var sut = new InMemoryProtocolRecorder(jobs);
        var protocolId = await sut.BeginAsync(job.Id, 1, "src/Foo.cs", null, AiConnectionModelCategory.Default, "gpt-5.4", CancellationToken.None);

        await sut.RecordPromptStageEvidenceAsync(
            protocolId,
            "per_file_user",
            "variant-a",
            PromptCompositionMode.Replace,
            false,
            "system prompt",
            "user prompt",
            CancellationToken.None);

        var protocol = Assert.Single(job.Protocols);
        var evt = Assert.Single(protocol.Events);
        Assert.Equal(ProtocolEventKind.Operational, evt.Kind);
        Assert.Equal(ReviewProtocolEventNames.PromptStageEvidenceRecorded, evt.Name);
        Assert.Equal("user prompt", evt.InputTextSample);
        Assert.Equal("system prompt", evt.SystemPrompt);
        Assert.Contains("\"stageKey\":\"per_file_user\"", evt.OutputSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordReviewStrategyEventAsync_SessionEvents_PreserveSessionPayloadsInMemory()
    {
        var jobs = new InMemoryReviewJobRepository();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 7);
        await jobs.AddAsync(job, CancellationToken.None);

        var sut = new InMemoryProtocolRecorder(jobs);
        var protocolId = await sut.BeginAsync(job.Id, 1, "src/Foo.cs", null, AiConnectionModelCategory.Default, "gpt-5.4", CancellationToken.None);

        await sut.RecordReviewStrategyEventAsync(
            protocolId,
            ReviewProtocolEventNames.ReviewAgentSessionBinding,
            "{\"sessionOwnerId\":\"session-1\",\"conversationOwnerId\":\"session-1\",\"bindingMethod\":\"created_remote_thread\",\"bindingOutcome\":\"succeeded\",\"remoteConversationId\":\"conv-1\"}",
            "{\"providerResponseId\":\"resp-1\"}",
            null,
            CancellationToken.None);
        await sut.RecordReviewStrategyEventAsync(
            protocolId,
            ReviewProtocolEventNames.ReviewAgentSessionTurn,
            "{\"turnNumber\":2,\"sessionMode\":\"LocalManagedSession\",\"contextStrategy\":\"CompactedReplay\"}",
            "{\"outputSample\":\"Compacted replay succeeded.\"}",
            null,
            CancellationToken.None);
        await sut.RecordReviewStrategyEventAsync(
            protocolId,
            ReviewProtocolEventNames.ReviewAgentSessionFallback,
            "{\"fromMode\":\"ProviderManagedSession\",\"toMode\":\"LocalManagedSession\",\"reason\":\"provider_session_continue_failed\"}",
            null,
            null,
            CancellationToken.None);

        var protocol = Assert.Single(job.Protocols);
        Assert.Equal(3, protocol.Events.Count);

        var bindingEvent = Assert.Single(protocol.Events, e => e.Name == ReviewProtocolEventNames.ReviewAgentSessionBinding);
        Assert.Equal(ProtocolEventKind.Operational, bindingEvent.Kind);
        Assert.Contains("created_remote_thread", bindingEvent.InputTextSample, StringComparison.Ordinal);
        Assert.Contains("resp-1", bindingEvent.OutputSummary, StringComparison.Ordinal);

        var turnEvent = Assert.Single(protocol.Events, e => e.Name == ReviewProtocolEventNames.ReviewAgentSessionTurn);
        Assert.Equal(ProtocolEventKind.Operational, turnEvent.Kind);
        Assert.Contains("CompactedReplay", turnEvent.InputTextSample, StringComparison.Ordinal);
        Assert.Contains("Compacted replay succeeded.", turnEvent.OutputSummary, StringComparison.Ordinal);

        var fallbackEvent = Assert.Single(protocol.Events, e => e.Name == ReviewProtocolEventNames.ReviewAgentSessionFallback);
        Assert.Equal(ProtocolEventKind.Operational, fallbackEvent.Kind);
        Assert.Contains("provider_session_continue_failed", fallbackEvent.InputTextSample, StringComparison.Ordinal);
        Assert.Null(fallbackEvent.OutputSummary);
    }

    [Fact]
    public async Task BeginAsync_WithPassKindAndReason_PersistsThemOnTheProtocol()
    {
        var jobs = new InMemoryReviewJobRepository();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 7);
        await jobs.AddAsync(job, CancellationToken.None);

        var sut = new InMemoryProtocolRecorder(jobs);
        await sut.BeginAsync(
            job.Id, 1, "Program.cs", null, AiConnectionModelCategory.MediumEffort, "gpt-5.4-mini", CancellationToken.None,
            ReviewPassKind.ProRVAugmentation, "high-risk file — re-reviewed in depth");

        var protocol = Assert.Single(job.Protocols);
        Assert.Equal(ReviewPassKind.ProRVAugmentation.ToString(), protocol.PassKind);
        Assert.Equal("high-risk file — re-reviewed in depth", protocol.Reason);
    }

    [Fact]
    public async Task BeginAsync_WithoutPassKind_LeavesPassIdentityNull()
    {
        var jobs = new InMemoryReviewJobRepository();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        await jobs.AddAsync(job, CancellationToken.None);

        var sut = new InMemoryProtocolRecorder(jobs);
        await sut.BeginAsync(job.Id, 1, "synthesis", null, AiConnectionModelCategory.HighEffort, "gpt-5.4", CancellationToken.None);

        var protocol = Assert.Single(job.Protocols);
        Assert.Null(protocol.PassKind);
        Assert.Null(protocol.Reason);
    }
}
