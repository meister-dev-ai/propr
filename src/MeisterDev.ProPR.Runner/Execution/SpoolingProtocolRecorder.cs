// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using System.Text.Json;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     The pipeline's trace recorder, on a host with no database.
///     <para>
///         Every event the in-process path writes to <c>protocol_events</c> is buffered here and sent
///         through ingest under the same event names, so a remote review's trace reads identically to a
///         local one. An operator opening the job protocol should not be able to tell which side ran it,
///         and a different vocabulary here would make that the first thing they noticed.
///     </para>
///     <para>
///         One member throws rather than buffer: the thread-pass protocol is control-plane work the runner
///         never performs, and a no-op would let a later caller assume something was recorded. Memory,
///         dedup, and publication events are buffered like every other kind, because the executor runs
///         synthesis-time deduplication and proxied memory reconsideration itself, and an event kind that
///         threw here would crash the first review that legitimately recorded one.
///     </para>
/// </summary>
public sealed class SpoolingProtocolRecorder(JobSpool spool, TimeProvider timeProvider) : IProtocolRecorder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Which model each open protocol is billing against. A pass's tokens arrive at
    ///     <see cref="SetCompletedAsync" />, which is given a protocol id and no model, so the name has to
    ///     be remembered from where it was named.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _protocolModels = new();

    /// <inheritdoc />
    public Task<Guid> BeginAsync(
        Guid jobId,
        int attemptNumber,
        string? label = null,
        Guid? fileResultId = null,
        AiConnectionModelCategory? connectionCategory = null,
        string? modelId = null,
        CancellationToken ct = default,
        ReviewPassKind? passKind = null,
        string? reason = null,
        string? logicalModelName = null)
    {
        // The protocol id is minted locally. The control plane keys ingested events by job and event
        // name, so the id only has to be stable within this execution for the pipeline to correlate on.
        var protocolId = Guid.NewGuid();
        if (!string.IsNullOrWhiteSpace(logicalModelName))
        {
            this._protocolModels[protocolId] = logicalModelName;
        }

        this.Record(
            "protocol.begin",
            new { protocolId, attemptNumber, label, passKind = passKind?.ToString(), modelId, logicalModelName, reason });

        return Task.FromResult(protocolId);
    }

    /// <inheritdoc />
    public Task<Guid> BeginForThreadPassAsync(
        Guid threadPassJobId,
        int attemptNumber,
        string? label = null,
        string? modelId = null,
        CancellationToken ct = default,
        string? logicalModelName = null)
    {
        throw new NotSupportedException("Thread passes run in the control plane; a runner reviews files.");
    }

    /// <inheritdoc />
    public Task<Guid> BeginForMentionReplyAsync(
        Guid mentionReplyJobId,
        string? label = null,
        string? modelId = null,
        CancellationToken ct = default,
        string? logicalModelName = null)
    {
        throw new NotSupportedException("Mention answers run in the control plane; a runner reviews files.");
    }

    /// <inheritdoc />
    public Task RecordAiCallAsync(
        Guid protocolId,
        int iteration,
        long? inputTokens,
        long? outputTokens,
        string? inputTextSample,
        string? systemPrompt,
        string? outputTextSample,
        CancellationToken ct = default,
        string? name = null,
        string? error = null,
        long? cachedInputTokens = null,
        CacheCallStatus cacheStatus = CacheCallStatus.NotApplicable,
        string? cacheMissCategory = null,
        PrefixEligibilityStatus prefixEligibility = PrefixEligibilityStatus.NotApplicable,
        string? finalizationAttemptKind = null,
        string? finalizationReason = null,
        string? finalizationOutcome = null,
        long? cacheWriteTokens = null,
        long? reasoningTokens = null)
    {
        this.Record(
            "protocol.ai_call",
            new
            {
                protocolId,
                iteration,
                inputTokens,
                outputTokens,
                inputTextSample,
                systemPrompt,
                outputTextSample,
                name,
                error,
                cachedInputTokens,
                cacheStatus = cacheStatus.ToString(),
                cacheMissCategory,
                prefixEligibility = prefixEligibility.ToString(),
                finalizationAttemptKind,
                finalizationReason,
                finalizationOutcome,
                cacheWriteTokens,
                reasoningTokens,
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordPromptStageEvidenceAsync(
        Guid protocolId,
        string stageKey,
        string variantName,
        PromptCompositionMode compositionMode,
        bool usedDefaultConstruction,
        string? systemPromptText,
        string? userPromptText,
        CancellationToken ct = default)
    {
        this.Record(
            "protocol.prompt_stage_evidence",
            new
            {
                protocolId,
                stageKey,
                variantName,
                compositionMode = compositionMode.ToString(),
                usedDefaultConstruction,
                systemPromptText,
                userPromptText,
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordToolCallAsync(
        Guid protocolId,
        string toolName,
        string arguments,
        string result,
        int iteration,
        CancellationToken ct = default,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        long? durationMs = null,
        long? waitDurationMs = null,
        long? activeDurationMs = null,
        string? timingAvailability = null,
        string? toolOutcome = null,
        IReadOnlyList<ProtocolEventPhaseTiming>? phaseTimings = null,
        string? toolEvidenceAction = null,
        int? toolEvidenceOriginalPayloadTokens = null,
        int? toolEvidenceBoundedPayloadTokens = null,
        bool? toolEvidenceRefreshable = null)
    {
        this.Record(
            "protocol.tool_call",
            new
            {
                protocolId,
                toolName,
                arguments,
                result,
                iteration,
                startedAt,
                completedAt,
                durationMs,
                waitDurationMs,
                activeDurationMs,
                timingAvailability,
                toolOutcome,
                phaseTimings,
                toolEvidenceAction,
                toolEvidenceOriginalPayloadTokens,
                toolEvidenceBoundedPayloadTokens,
                toolEvidenceRefreshable,
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetCompletedAsync(
        Guid protocolId,
        string outcome,
        long totalInputTokens,
        long totalOutputTokens,
        int iterationCount,
        int toolCallCount,
        int? finalConfidence,
        CancellationToken ct = default,
        long? totalCachedInputTokens = null,
        CacheObservabilityStatus cacheObservability = CacheObservabilityStatus.Unknown,
        long? totalCacheWriteTokens = null,
        long? totalReasoningTokens = null)
    {
        // Where a pass's tokens are recorded. The pipeline accrues most of what a review spends here rather
        // than through AddTokensAsync, and the trace events this recorder sends are stored as opaque detail,
        // so a review whose spend was only read from the trace reported nothing at all.
        if (this._protocolModels.TryGetValue(protocolId, out var logicalModelName)
            && (totalInputTokens > 0 || totalOutputTokens > 0))
        {
            spool.Add(
                new RunnerSpendRecord(
                    logicalModelName,
                    totalInputTokens,
                    totalOutputTokens,
                    null));
        }

        this._protocolModels.TryRemove(protocolId, out _);

        this.Record(
            "protocol.completed",
            new
            {
                protocolId,
                outcome,
                totalInputTokens,
                totalOutputTokens,
                iterationCount,
                toolCallCount,
                finalConfidence,
                totalCachedInputTokens,
                cacheObservability = cacheObservability.ToString(),
                totalCacheWriteTokens,
                totalReasoningTokens,
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddTokensAsync(
        Guid protocolId,
        long inputTokens,
        long outputTokens,
        AiConnectionModelCategory? connectionCategory = null,
        string? modelId = null,
        CancellationToken ct = default,
        long cachedInputTokens = 0,
        long cacheWriteTokens = 0,
        long reasoningTokens = 0,
        string? logicalModelName = null)
    {
        // Spend rather than a trace line. The control plane prices it on arrival, the same way it prices
        // an in-process review, so a remote job's cost is computed by the same code as a local one's.
        if (!string.IsNullOrWhiteSpace(logicalModelName))
        {
            spool.Add(
                new RunnerSpendRecord(
                    logicalModelName,
                    inputTokens,
                    outputTokens,
                    null));
        }

        this.Record(
            "protocol.tokens",
            new { protocolId, inputTokens, outputTokens, modelId, logicalModelName, cachedInputTokens, cacheWriteTokens, reasoningTokens });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordMemoryEventAsync(Guid protocolId, string eventName, string? details, string? error, CancellationToken ct = default)
    {
        // The store behind thread memory lives in the control plane, but the lookup is proxied, so what the
        // executor reports about it (ran, degraded, refused) belongs on this trace like any other stage.
        return this.RecordStage("protocol.memory", protocolId, eventName, details, null, error);
    }

    /// <inheritdoc />
    public Task RecordDedupEventAsync(Guid protocolId, string eventName, string? details, string? error, CancellationToken ct = default)
    {
        // Synthesis-time deduplication runs on the executor; only the publication-time layer stays central.
        return this.RecordStage("protocol.dedup", protocolId, eventName, details, null, error);
    }

    /// <inheritdoc />
    public Task RecordPublicationEventAsync(Guid protocolId, string eventName, string? details, string? error, CancellationToken ct = default)
    {
        // Publication itself is control-plane work. Buffered anyway: if executor-side code ever records
        // one, a trace showing where it came from beats a crashed review showing nothing.
        return this.RecordStage("protocol.publication", protocolId, eventName, details, null, error);
    }

    /// <inheritdoc />
    public Task RecordCommentRelevanceEventAsync(
        Guid protocolId, string eventName, string? details, string? output, string? error, CancellationToken ct = default)
    {
        return this.RecordStage("protocol.comment_relevance", protocolId, eventName, details, output, error);
    }

    /// <inheritdoc />
    public Task RecordReviewFindingGateEventAsync(
        Guid protocolId, string eventName, string? details, string? output, string? error, CancellationToken ct = default)
    {
        return this.RecordStage("protocol.finding_gate", protocolId, eventName, details, output, error);
    }

    /// <inheritdoc />
    public Task RecordVerificationEventAsync(Guid protocolId, string eventName, string? details, string? output, string? error, CancellationToken ct = default)
    {
        return this.RecordStage("protocol.verification", protocolId, eventName, details, output, error);
    }

    /// <inheritdoc />
    public Task RecordReviewStrategyEventAsync(
        Guid protocolId, string eventName, string? details, string? output, string? error, CancellationToken ct = default)
    {
        return this.RecordStage("protocol.review_strategy", protocolId, eventName, details, output, error);
    }

    /// <inheritdoc />
    public Task RecordPrWideStageEventAsync(Guid protocolId, string eventName, string? details, string? output, string? error, CancellationToken ct = default)
    {
        return this.RecordStage("protocol.pr_wide_stage", protocolId, eventName, details, output, error);
    }

    /// <inheritdoc />
    public Task RecordProRvEventAsync(Guid protocolId, string eventName, string? details, string? output, string? error, CancellationToken ct = default)
    {
        return this.RecordStage("protocol.prorv", protocolId, eventName, details, output, error);
    }

    /// <inheritdoc />
    public Task RecordLogicalModelResolutionEventAsync(
        Guid protocolId, string eventName, string? details, string? output, string? error, CancellationToken ct = default)
    {
        return this.RecordStage("protocol.logical_model_resolution", protocolId, eventName, details, output, error);
    }

    private Task RecordStage(string channel, Guid protocolId, string eventName, string? details, string? output, string? error)
    {
        this.Record(channel, new { protocolId, eventName, details, output, error });
        return Task.CompletedTask;
    }

    private void Record(string name, object payload)
    {
        spool.Add(name, JsonSerializer.Serialize(payload, Json), timeProvider.GetUtcNow());
    }
}
