// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     What an ingested per-file outcome becomes on disk. These rows are the resume checkpoints a reclaimed
///     job reads back, so anything the wire carried but the row lost, findings in particular, is missing from
///     what a resumed review publishes.
/// </summary>
public sealed class RunnerIngestWriterTests
{
    private static readonly Guid JobId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>What an operator reading the trace sees in place of a sample that was not stored.</summary>
    private const string WithheldNote = "[withheld: this tenant does not capture model reasoning]";

    private readonly IJobRepository _jobs = Substitute.For<IJobRepository>();
    private readonly IProtocolRecorder _protocols = Substitute.For<IProtocolRecorder>();

    private readonly IRunnerJobReasoningCapturePolicy _reasoningCapture =
        Substitute.For<IRunnerJobReasoningCapturePolicy>();

    private static ReviewJob Job(params ReviewFileResult[] rows)
    {
        var job = new ReviewJob(JobId, Guid.NewGuid(), "https://host.invalid", "proj", "repo", 12, 1);
        foreach (var row in rows)
        {
            job.FileReviewResults.Add(row);
        }

        return job;
    }

    private RunnerIngestWriter CreateWriter(ReviewJob job)
    {
        this._jobs.GetByIdWithFileResultsAsync(JobId, Arg.Any<CancellationToken>()).Returns(job);
        return new RunnerIngestWriter(this._jobs, this._protocols, this._reasoningCapture);
    }

    // The comments are the checkpoint's substance: without them a reclaimed job's synthesis reasons over
    // finished files that appear to have found nothing, and the published review drops the findings.
    [Fact]
    public async Task ACompletedOutcome_PersistsItsComments()
    {
        ReviewFileResult? written = null;
        this._jobs.AddFileResultAsync(Arg.Do<ReviewFileResult>(result => written = result), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var writer = this.CreateWriter(Job());

        await writer.WriteFileResultsAsync(
            JobId,
            [
                new RunnerFileOutcome(
                    "src/a.cs", true, false, "looks fine", null, ["pass-1"],
                    [new ReviewComment("src/a.cs", 3, CommentSeverity.Warning, "Bounded?")]),
            ]);

        Assert.NotNull(written);
        Assert.True(written!.IsComplete);
        var comment = Assert.Single(written.Comments!);
        Assert.Equal("Bounded?", comment.Message);
        Assert.Equal(3, comment.LineNumber);
    }

    [Fact]
    public async Task AnExcludedOutcome_IsPersistedAsExcluded()
    {
        ReviewFileResult? written = null;
        this._jobs.AddFileResultAsync(Arg.Do<ReviewFileResult>(result => written = result), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var writer = this.CreateWriter(Job());

        await writer.WriteFileResultsAsync(
            JobId,
            [new RunnerFileOutcome("src/generated.cs", false, false, null, null, [], IsExcluded: true, ExclusionReason: "**/generated/**")]);

        Assert.NotNull(written);
        Assert.True(written!.IsExcluded);
        Assert.Equal("**/generated/**", written.ExclusionReason);
    }

    // A file that failed on one attempt and succeeded on the next arrives as two outcomes across batches.
    // The entity refuses to complete a failed row, so the writer has to clear the earlier mark first.
    [Fact]
    public async Task AFailedRow_IsUpgradedByARetrysCompletion()
    {
        var failed = new ReviewFileResult(JobId, "src/a.cs");
        failed.MarkFailed("the model refused");
        var writer = this.CreateWriter(Job(failed));

        await writer.WriteFileResultsAsync(
            JobId,
            [
                new RunnerFileOutcome(
                    "src/a.cs", true, false, "second try worked", null, ["pass-1"],
                    [new ReviewComment("src/a.cs", 1, CommentSeverity.Suggestion, "nit")]),
            ]);

        await this._jobs.Received(1).UpdateFileResultAsync(failed, Arg.Any<CancellationToken>());
        Assert.True(failed.IsComplete);
        Assert.False(failed.IsFailed);
        Assert.Single(failed.Comments!);
    }

    // Pricing is per physical model: a spend protocol carrying only the logical name priced against
    // nothing, and a remote review's cost stayed null however much it spent.
    [Fact]
    public async Task AnIngestedSpendRecord_CarriesThePhysicalModelWhenTheLogicalNameResolves()
    {
        var job = Job();
        this._jobs.GetById(JobId).Returns(job);
        var logicalModels = Substitute.For<MeisterDev.ProPR.Application.Interfaces.ILogicalModelResolver>();
        var runtime = Substitute.For<MeisterDev.ProPR.Application.Interfaces.IResolvedAiChatRuntime>();
        runtime.Model.Returns(
            new MeisterDev.ProPR.Application.DTOs.AiConfiguredModelDto(
                Guid.NewGuid(), "gpt-5-mini", "Reviewer",
                [MeisterDev.Ai.Providers.Enums.AiOperationKind.Chat],
                [MeisterDev.Ai.Providers.Declaration.ProviderDeclaredProtocolModes.Auto]));
        logicalModels.ResolveChatRuntimeAsync(
                job.ClientId, "reviewer-medium",
                Arg.Any<MeisterDev.ProPR.Application.Interfaces.IProtocolRecorder?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(
                new MeisterDev.ProPR.Application.DTOs.ResolvedLogicalModelChatRuntime(
                    runtime, "reviewer-medium",
                    MeisterDev.ProPR.Domain.Enums.LogicalModelLayer.TenantCatalog,
                    MeisterDev.ProPR.Domain.Enums.ReviewReasoningEffort.None));
        var writer = new RunnerIngestWriter(this._jobs, this._protocols, this._reasoningCapture, logicalModels);

        await writer.WriteSpendAsync(JobId, [new RunnerSpendRecord("reviewer-medium", 100, 20, null)]);

        await this._protocols.Received(1).BeginAsync(
            JobId, 1, "runner-relay:reviewer-medium",
            Arg.Any<Guid?>(), Arg.Any<MeisterDev.ProPR.Domain.Enums.AiConnectionModelCategory?>(),
            "gpt-5-mini",
            Arg.Any<CancellationToken>(),
            Arg.Any<MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models.ReviewPassKind?>(), Arg.Any<string?>(),
            "reviewer-medium");
    }

    // Unpriced beats unrecorded: the tokens are already spent, so a binding deleted since dispatch must
    // not cost the job its usage record.
    [Fact]
    public async Task ASpendRecordWhoseLogicalNameNoLongerResolves_IsStillRecordedUnpriced()
    {
        var job = Job();
        this._jobs.GetById(JobId).Returns(job);
        var logicalModels = Substitute.For<MeisterDev.ProPR.Application.Interfaces.ILogicalModelResolver>();
        logicalModels.ResolveChatRuntimeAsync(
                Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<MeisterDev.ProPR.Application.Interfaces.IProtocolRecorder?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns<MeisterDev.ProPR.Application.DTOs.ResolvedLogicalModelChatRuntime>(_ => throw new InvalidOperationException("binding deleted"));
        var writer = new RunnerIngestWriter(this._jobs, this._protocols, this._reasoningCapture, logicalModels);

        await writer.WriteSpendAsync(JobId, [new RunnerSpendRecord("reviewer-medium", 100, 20, null)]);

        await this._protocols.Received(1).BeginAsync(
            JobId, 1, "runner-relay:reviewer-medium",
            Arg.Any<Guid?>(), Arg.Any<MeisterDev.ProPR.Domain.Enums.AiConnectionModelCategory?>(),
            null,
            Arg.Any<CancellationToken>(),
            Arg.Any<MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models.ReviewPassKind?>(), Arg.Any<string?>(),
            "reviewer-medium");
        await this._protocols.Received(1).SetCompletedAsync(
            Arg.Any<Guid>(), "Completed", 100, 20, 0, 0, null, Arg.Any<CancellationToken>(), 0, Arg.Any<CacheObservabilityStatus>(), 0, 0);
    }

    // The control plane prices the input total less the two cache buckets at the input rate and each bucket at
    // its own, so a relayed pass whose buckets were dropped on the way in is charged the full rate for a prompt
    // the provider served from cache. All five counters reach the protocol the pricing pass reads.
    [Fact]
    public async Task ASpendRecordsCacheAndReasoningCountersReachTheProtocolThePricingReads()
    {
        this._jobs.GetById(JobId).Returns(Job());
        var logicalModels = Substitute.For<MeisterDev.ProPR.Application.Interfaces.ILogicalModelResolver>();
        logicalModels.ResolveChatRuntimeAsync(
                Arg.Any<Guid>(), Arg.Any<string>(),
                Arg.Any<MeisterDev.ProPR.Application.Interfaces.IProtocolRecorder?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns<MeisterDev.ProPR.Application.DTOs.ResolvedLogicalModelChatRuntime>(_ => throw new InvalidOperationException("binding deleted"));
        var writer = new RunnerIngestWriter(this._jobs, this._protocols, this._reasoningCapture, logicalModels);

        await writer.WriteSpendAsync(
            JobId,
            [new RunnerSpendRecord("reviewer-medium", 4170, 207, null, 4000, 50, 200)]);

        await this._protocols.Received(1).SetCompletedAsync(
            Arg.Any<Guid>(), "Completed", 4170, 207, 0, 0, null, Arg.Any<CancellationToken>(),
            4000, Arg.Any<CacheObservabilityStatus>(), 50, 200);
    }

    // An excluded row is a checkpoint like a completed one: a replayed batch must not overwrite it, and
    // re-marking it would throw and take the whole batch down, spend and trace included.
    [Fact]
    public async Task AnExcludedRow_IsNotTouchedByAReplay()
    {
        var excluded = new ReviewFileResult(JobId, "src/generated.cs");
        excluded.MarkExcluded("**/generated/**");
        var writer = this.CreateWriter(Job(excluded));

        await writer.WriteFileResultsAsync(
            JobId,
            [new RunnerFileOutcome("src/generated.cs", false, false, null, null, [], IsExcluded: true, ExclusionReason: "**/generated/**")]);

        await this._jobs.DidNotReceive().UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>());
        await this._jobs.DidNotReceive().AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>());
    }

    // A runner decides for itself what to spool, so an outdated or hostile one can send reasoning a tenant has
    // forbidden. It is removed here, where the text is about to be stored, and it goes from both columns the
    // event is replayed into.
    [Fact]
    public async Task ATenantThatWithholdsReasoning_GetsItRemovedFromAnIngestedAiCall()
    {
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);
        var written = await this.WriteAiCallAsync(SpooledAiCall(AssistantTurn(withReasoning: true)));

        Assert.NotNull(written);
        var details = JsonDocument.Parse(written!).RootElement;
        var turn = JsonDocument.Parse(details.GetProperty("outputTextSample").GetString()!).RootElement;
        Assert.False(turn.TryGetProperty("reasoning", out _));
        Assert.Equal("Done.", turn.GetProperty("assistantText").GetString());
        Assert.DoesNotContain("private chain of thought", written!, StringComparison.Ordinal);

        // Budgets are computed from the counts, so they stay where they are.
        Assert.Equal(128, details.GetProperty("reasoningTokens").GetInt64());
    }

    [Fact]
    public async Task ATenantThatCapturesReasoning_GetsTheIngestedAiCallUnchanged()
    {
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(true);
        var spooled = SpooledAiCall(AssistantTurn(withReasoning: true));

        var written = await this.WriteAiCallAsync(spooled);

        Assert.Equal(spooled, written);
    }

    // The sample cannot be read into its parts, so it cannot be shown to hold no reasoning.
    [Fact]
    public async Task ATenantThatWithholdsReasoning_GetsAnUnreadableSampleReplacedByANote()
    {
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);

        var written = await this.WriteAiCallAsync(SpooledAiCall("the model answered in prose"));

        Assert.NotNull(written);
        Assert.DoesNotContain("the model answered in prose", written!, StringComparison.Ordinal);
        var sample = JsonDocument.Parse(written!).RootElement.GetProperty("outputTextSample").GetString();
        Assert.Equal(WithheldNote, sample);
    }

    [Fact]
    public async Task ATenantThatWithholdsReasoning_GetsAnUnreadableEventReplacedByANote()
    {
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);

        var written = await this.WriteAiCallAsync("not json at all");

        Assert.Equal(WithheldNote, written);
    }

    // A runner that spooled the assistant turn as structured JSON instead of a serialized string. The sample
    // cannot be read into its parts here, so it cannot be shown to hold no reasoning and the whole sample goes.
    [Fact]
    public async Task ATenantThatWithholdsReasoning_GetsANonStringSampleReplacedByANote()
    {
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);
        var spooled = JsonSerializer.Serialize(
            new
            {
                protocolId = Guid.Empty,
                iteration = 1,
                inputTokens = 2048,
                outputTokens = 50,
                inputTextSample = "[system] …",
                outputTextSample = new { assistantText = "Done.", reasoning = "private chain of thought" },
                reasoningTokens = 128,
            });

        var written = await this.WriteAiCallAsync(spooled);

        Assert.NotNull(written);
        var details = JsonDocument.Parse(written!).RootElement;

        // The whole sample is replaced, so neither the reasoning nor the answer beside it survives: a sample
        // this side cannot read into its parts cannot be trimmed field by field.
        Assert.DoesNotContain("private chain of thought", written!, StringComparison.Ordinal);
        Assert.DoesNotContain("Done.", written!, StringComparison.Ordinal);
        Assert.Equal(WithheldNote, details.GetProperty("outputTextSample").GetString());

        // Budgets are computed from the counts, so they stay where they are.
        Assert.Equal(128, details.GetProperty("reasoningTokens").GetInt64());
    }

    // Only the AI calls carry the model's reasoning; the rest of the trace is left alone.
    [Fact]
    public async Task ATenantThatWithholdsReasoning_GetsOtherTraceEventsUnchanged()
    {
        this._reasoningCapture.CapturesReasoningAsync(JobId, Arg.Any<CancellationToken>()).Returns(false);
        const string spooled = """{"stage":"planning","note":"not an ai call"}""";

        var written = await this.WriteAiCallAsync(spooled, eventName: "protocol.strategy_event");

        Assert.Equal(spooled, written);
    }

    private async Task<string?> WriteAiCallAsync(string details, string eventName = "protocol.ai_call")
    {
        var protocolId = Guid.NewGuid();
        this._protocols
            .BeginAsync(
                JobId, 1, "runner-trace", Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(),
                Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(protocolId);

        string? outputSummary = null;
        string? inputSample = null;
        await this._protocols.RecordReviewStrategyEventAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Do<string?>(value => inputSample = value),
            Arg.Do<string?>(value => outputSummary = value),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        var writer = new RunnerIngestWriter(this._jobs, this._protocols, this._reasoningCapture);
        await writer.WriteEventsAsync(JobId, [new RunnerTraceEvent(DateTimeOffset.UtcNow, eventName, details)]);

        // Both columns carry the same replayed blob, so a strip that reached only one of them would still
        // leave the text on the row.
        Assert.Equal(outputSummary, inputSample);
        return outputSummary;
    }

    private static string AssistantTurn(bool withReasoning)
    {
        return withReasoning
            ? """{"assistantText":"Done.","reasoning":"private chain of thought"}"""
            : """{"assistantText":"Done."}""";
    }

    private static string SpooledAiCall(string outputTextSample)
    {
        return JsonSerializer.Serialize(
            new
            {
                protocolId = Guid.Empty,
                iteration = 1,
                inputTokens = 2048,
                outputTokens = 50,
                inputTextSample = "[system] …",
                outputTextSample,
                reasoningTokens = 128,
            });
    }
}
