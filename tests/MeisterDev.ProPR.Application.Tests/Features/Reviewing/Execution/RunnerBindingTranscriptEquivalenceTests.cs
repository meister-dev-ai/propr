// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     Runs one recorded fixture through the same review flow twice, once per binding, and compares what
///     each produced.
///     <para>
///         Equivalence is asserted on the tool-call transcript, the sequence of persisted writes, and the
///         findings, rather than on live model text. Model output is not deterministic, so comparing it
///         would produce a suite that fails for reasons that have nothing to do with the bindings and gets
///         disabled within a month.
///     </para>
/// </summary>
public sealed class RunnerBindingTranscriptEquivalenceTests
{
    private static readonly Guid JobId = Guid.Parse("13131313-1313-1313-1313-131313131313");
    private static readonly RunnerCallContext Call = new(JobId, 1, "runner-a");

    /// <summary>
    ///     A deterministic stand-in for the review pipeline. It is not the real orchestration, but it is the
    ///     part that differs between bindings: it reaches the review-context tools, asks a model, and emits
    ///     findings and protocol events. Both bindings run this exact code.
    /// </summary>
    private static async Task<(IReadOnlyList<string> Transcript, IReadOnlyList<ReviewComment> Findings)> RunFlowAsync(
        IReviewContextTools tools,
        IChatClient chat,
        Action<string> record)
    {
        var findings = new List<ReviewComment>();

        var changed = await tools.GetChangedFilesAsync(CancellationToken.None);
        record($"changed_files:{changed.Count}");

        foreach (var file in changed.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var content = await tools.GetFileContentAsync(file.Path, "head", 1, 100, CancellationToken.None);
            record($"file_content:{file.Path}:{content.Length}");

            var knowledge = await tools.AskProCursorKnowledgeAsync($"what is {file.Path}", CancellationToken.None);
            record($"knowledge:{file.Path}:{knowledge.Status}");

            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.User, $"review {file.Path}")],
                null,
                CancellationToken.None);
            record($"completion:{file.Path}:{response.Text}");

            findings.Add(new ReviewComment(file.Path, 1, CommentSeverity.Suggestion, $"reviewed {file.Path}"));
        }

        var discussion = await tools.GetLinkedItemDiscussionAsync("AB#1", CancellationToken.None);
        record($"linked_discussion:{discussion.Count}");

        return ([], findings);
    }

    private static IReviewContextTools Fixture()
    {
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ChangedFileSummary>>(_ =>
            [
                new ChangedFileSummary("src/b.cs", ChangeType.Add),
                new ChangedFileSummary("src/a.cs", ChangeType.Edit),
            ]);
        tools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => $"contents of {call.ArgAt<string>(0)}");
        tools.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("answered", [], null));
        tools.GetLinkedItemDiscussionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<LinkedItemComment>>(_ => []);
        return tools;
    }

    /// <summary>A chat client that answers the same way every time, so the transcript is comparable.</summary>
    private static IChatClient ScriptedChat()
    {
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "looks fine")));
        return chat;
    }

    private static (IReviewContextTools Tools, IChatClient Chat) ProxyBinding(
        IReviewContextTools fixture,
        IChatClient scripted)
    {
        var authorizer = Substitute.For<IRunnerCallAuthorizer>();
        authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(RunnerCallAuthorization.Allow(Guid.NewGuid()));

        var registry = new RunnerJobToolsRegistry();
        registry.Register(JobId, fixture, codeKnowledgeOffered: true);
        var tools = new ProxyReviewContextTools(Call, new RunnerToolProxy(authorizer, registry), fixture);

        var budgets = new RunnerJobBudgetRegistry();
        budgets.Register(
            JobId,
            new BudgetScope(
                BudgetCaps.None,
                new ReviewSpendBaseline(ReviewScopeSpend.None, ReviewScopeSpend.None, ReviewScopeSpend.None)));
        var models = Substitute.For<IRunnerRelayModelResolver>();
        models.ResolveAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new RunnerRelayModel(scripted, AiProviderKind.OpenAi, new ModelPricing(null, null)));
        var usage = Substitute.For<IRunnerRelayUsageRecorder>();

        var relay = new RunnerAiRelay(authorizer, budgets, models, usage, new RunnerRelayReplayCache());
        var counter = 0;
        var chat = new RelayChatClient(Call, "reviewer-medium", relay, () => $"call-{++counter}");

        return (tools, chat);
    }

    // The comparison the market requirement specifies: identical tool-call transcripts and identical
    // findings, from one fixture run twice. Anything that differs between the bindings shows up here as a
    // line that does not match, which is the only way a subtly wrong proxy adapter is ever noticed.
    [Fact]
    public async Task OneFixtureRunUnderBothBindings_ProducesTheSameTranscriptAndTheSameFindings()
    {
        var fixture = Fixture();
        var scripted = ScriptedChat();

        var directTranscript = new List<string>();
        var (_, directFindings) = await RunFlowAsync(fixture, scripted, directTranscript.Add);

        var (proxyTools, proxyChat) = ProxyBinding(Fixture(), ScriptedChat());
        var proxyTranscript = new List<string>();
        var (_, proxyFindings) = await RunFlowAsync(proxyTools, proxyChat, proxyTranscript.Add);

        Assert.Equal(directTranscript, proxyTranscript);
        Assert.Equal(
            directFindings.Select(f => (f.FilePath, f.Message)),
            proxyFindings.Select(f => (f.FilePath, f.Message)));
    }

    // The sequence of persisted writes, which is the other half of what the requirement asks to compare.
    // Under the proxy binding the writes arrive as ingest batches; the order inside them has to match the
    // order the direct binding wrote in, or the trace reads differently for a remote review.
    [Fact]
    public async Task ThePersistedWriteSequence_IsTheSameUnderBothBindings()
    {
        var fixture = Fixture();
        var directWrites = new List<string>();
        await RunFlowAsync(fixture, ScriptedChat(), directWrites.Add);

        var (proxyTools, proxyChat) = ProxyBinding(Fixture(), ScriptedChat());
        var remoteWrites = new List<string>();
        await RunFlowAsync(proxyTools, proxyChat, remoteWrites.Add);

        // Sent the way a runner would send them, then read back in arrival order.
        var writer = new RecordingIngestWriter();
        var ledger = new InMemoryIngestLedger();
        var authorizer = Substitute.For<IRunnerCallAuthorizer>();
        authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(RunnerCallAuthorization.Allow(Guid.NewGuid()));
        var ingest = new RunnerIngestService(
            authorizer,
            ledger,
            writer,
            Microsoft.Extensions.Options.Options.Create(new RunnerIngestOptions()));

        var sequence = 1;
        foreach (var chunk in remoteWrites.Chunk(2))
        {
            var result = await ingest.IngestAsync(
                Call,
                new RunnerIngestBatch(
                    sequence,
                    $"batch-{sequence}",
                    [.. chunk.Select(line => new RunnerTraceEvent(DateTimeOffset.UnixEpoch, line, null))],
                    [],
                    []));
            Assert.Equal(RunnerIngestOutcome.Applied, result.Outcome);
            sequence++;
        }

        Assert.Equal(directWrites, writer.Names);
    }

    // Batching must not reorder. A trace whose events arrive in a different order is a different trace,
    // however faithful each individual event is.
    [Fact]
    public async Task ReplayingABatchMidStream_LeavesTheWriteSequenceUnchanged()
    {
        var writer = new RecordingIngestWriter();
        var ledger = new InMemoryIngestLedger();
        var authorizer = Substitute.For<IRunnerCallAuthorizer>();
        authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(RunnerCallAuthorization.Allow(Guid.NewGuid()));
        var ingest = new RunnerIngestService(authorizer, ledger, writer, Microsoft.Extensions.Options.Options.Create(new RunnerIngestOptions()));

        async Task ShipAsync(int sequence, params string[] events)
        {
            await ingest.IngestAsync(
                Call,
                new RunnerIngestBatch(
                    sequence,
                    $"batch-{sequence}",
                    [.. events.Select(e => new RunnerTraceEvent(DateTimeOffset.UnixEpoch, e, null))],
                    [],
                    []));
        }

        await ShipAsync(1, "a", "b");
        await ShipAsync(2, "c");
        await ShipAsync(1, "a", "b"); // the resend
        await ShipAsync(3, "d");

        Assert.Equal(["a", "b", "c", "d"], writer.Names);
    }

    private sealed class RecordingIngestWriter : IRunnerIngestWriter
    {
        public List<string> Names { get; } = [];

        public Task WriteEventsAsync(Guid jobId, IReadOnlyList<RunnerTraceEvent> events, CancellationToken ct = default)
        {
            this.Names.AddRange(events.Select(e => e.Name));
            return Task.CompletedTask;
        }

        public Task WriteFileResultsAsync(
            Guid jobId,
            IReadOnlyList<RunnerFileOutcome> results,
            CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task WriteSpendAsync(Guid jobId, IReadOnlyList<RunnerSpendRecord> spend, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryIngestLedger : IRunnerIngestLedger
    {
        private readonly HashSet<int> _applied = [];

        public Task<int> GetExpectedSequenceAsync(Guid jobId, CancellationToken ct = default)
        {
            return Task.FromResult((this._applied.Count == 0 ? 0 : this._applied.Max()) + 1);
        }

        public Task<bool> TryRecordAsync(
            Guid jobId,
            int sequence,
            string idempotencyKey,
            CancellationToken ct = default)
        {
            return Task.FromResult(this._applied.Add(sequence));
        }

        public Task ClearAsync(Guid jobId, CancellationToken ct = default)
        {
            this._applied.Clear();
            return Task.CompletedTask;
        }
    }
}
