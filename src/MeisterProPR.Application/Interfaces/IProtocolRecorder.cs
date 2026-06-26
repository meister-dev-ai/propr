// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Write-only service for recording agentic review protocol events incrementally during job execution.
///     All methods except <see cref="BeginAsync" /> must never throw — failures are caught and logged internally.
///     Protocol data is read back through
///     <see cref="IJobRepository.GetByIdWithProtocolAsync" /> (via the
///     <see cref="MeisterProPR.Domain.Entities.ReviewJob" /> aggregate root).
/// </summary>
public interface IProtocolRecorder
{
    /// <summary>
    ///     Creates a new protocol record for the given job attempt and returns its identifier.
    ///     Unlike the other methods, this call is allowed to throw on failure — the caller decides
    ///     whether to continue without a protocol.
    /// </summary>
    /// <param name="jobId">The review job this protocol belongs to.</param>
    /// <param name="attemptNumber">Attempt ordinal (1-based).</param>
    /// <param name="label">Human-readable label for this protocol pass (e.g. file path or "synthesis").</param>
    /// <param name="fileResultId">The file result this protocol pass belongs to, or null.</param>
    /// <param name="connectionCategory">The AI connection category (effort tier) used for this pass. Null for legacy callers.</param>
    /// <param name="modelId">The effective AI model deployment name used for this pass. Null for legacy callers.</param>
    /// <param name="passKind">The kind of review pass (baseline vs augmentation). Null when not meaningful (e.g. synthesis).</param>
    /// <param name="reason">Human-readable reason this pass ran. Null for the baseline pass.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new protocol record's <see cref="Guid" />.</returns>
    Task<Guid> BeginAsync(
        Guid jobId,
        int attemptNumber,
        string? label = null,
        Guid? fileResultId = null,
        AiConnectionModelCategory? connectionCategory = null,
        string? modelId = null,
        CancellationToken ct = default,
        ReviewPassKind? passKind = null,
        string? reason = null);

    /// <summary>
    ///     Records a single AI call event. Never throws.
    /// </summary>
    /// <param name="protocolId">The protocol this event belongs to.</param>
    /// <param name="iteration">Loop iteration number (one-based).</param>
    /// <param name="inputTokens">Input token count reported by the AI provider, or <see langword="null" />.</param>
    /// <param name="outputTokens">Output token count reported by the AI provider, or <see langword="null" />.</param>
    /// <param name="inputTextSample">Truncated input text (≤ 50,000 characters), or <see langword="null" />.</param>
    /// <param name="systemPrompt">Truncated system prompt text (≤ 50,000 characters), or <see langword="null" />.</param>
    /// <param name="outputTextSample">Truncated AI response text (≤ 50,000 characters), or <see langword="null" />.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="name">
    ///     Override the auto-generated event name <c>ai_call_iter_{iteration}</c>. Use for out-of-loop AI calls
    ///     such as <c>ai_call_memory_reconsideration</c>.
    /// </param>
    /// <param name="error">Error message when the AI call failed before returning usable output, or <see langword="null" />.</param>
    Task RecordAiCallAsync(
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
        string? finalizationOutcome = null);

    /// <summary>
    ///     Records explicit prompt-stage evidence for one executed prompt-driven stage. Never throws.
    /// </summary>
    /// <param name="protocolId">The protocol this evidence belongs to.</param>
    /// <param name="stageKey">The key of the stage for which the evidence is recorded (e.g. "drafting", "pro_rv_prefilter", etc.).</param>
    /// <param name="variantName">The name of the prompt experiment variant, or null if not applicable.</param>
    /// <param name="compositionMode">The composition mode of the prompt experiment, or null if not applicable.</param>
    /// <param name="usedDefaultConstruction">Indicates whether the default construction was used for this stage.</param>
    /// <param name="systemPromptText">The system prompt text used for this stage, or null if not applicable.</param>
    /// <param name="userPromptText">The user prompt text used for this stage, or null if not applicable.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordPromptStageEvidenceAsync(
        Guid protocolId,
        string stageKey,
        string variantName,
        PromptCompositionMode compositionMode,
        bool usedDefaultConstruction,
        string? systemPromptText,
        string? userPromptText,
        CancellationToken ct = default);

    /// <summary>
    ///     Records a single tool call event. Never throws.
    /// </summary>
    /// <param name="protocolId">The protocol this event belongs to.</param>
    /// <param name="toolName">Name of the tool that was invoked.</param>
    /// <param name="arguments">Serialised arguments passed to the tool.</param>
    /// <param name="result">Serialised result returned by the tool.</param>
    /// <param name="iteration">Current loop iteration number (1-based).</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordToolCallAsync(
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
        bool? toolEvidenceRefreshable = null);

    /// <summary>
    ///     Marks the protocol as complete and persists all aggregate summary fields. Never throws.
    /// </summary>
    /// <param name="protocolId">The protocol to close.</param>
    /// <param name="outcome"><c>"completed"</c> or <c>"failed"</c>.</param>
    /// <param name="totalInputTokens">Sum of input tokens across all AI calls.</param>
    /// <param name="totalOutputTokens">Sum of output tokens across all AI calls.</param>
    /// <param name="iterationCount">Number of loop iterations completed.</param>
    /// <param name="toolCallCount">Total number of tool invocations.</param>
    /// <param name="finalConfidence">Final aggregated confidence score, or <see langword="null" />.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetCompletedAsync(
        Guid protocolId,
        string outcome,
        long totalInputTokens,
        long totalOutputTokens,
        int iterationCount,
        int toolCallCount,
        int? finalConfidence,
        CancellationToken ct = default,
        long? totalCachedInputTokens = null,
        CacheObservabilityStatus cacheObservability = CacheObservabilityStatus.Unknown);

    /// <summary>
    ///     Adds token counts to an existing protocol's totals and propagates them to the job aggregate. Never throws.
    ///     Use when an out-of-loop AI call (e.g. memory reconsideration) completes <b>after</b>
    ///     <see cref="SetCompletedAsync" /> has already been called with the loop-only metrics.
    /// </summary>
    /// <param name="protocolId">The protocol to update.</param>
    /// <param name="inputTokens">Additional input tokens to add.</param>
    /// <param name="outputTokens">Additional output tokens to add.</param>
    /// <param name="connectionCategory">
    ///     The AI connection category for this out-of-loop call. Null to skip tier breakdown
    ///     update.
    /// </param>
    /// <param name="modelId">The effective model ID for this out-of-loop call. Null to skip tier breakdown update.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddTokensAsync(
        Guid protocolId,
        long inputTokens,
        long outputTokens,
        AiConnectionModelCategory? connectionCategory = null,
        string? modelId = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Records a memory system operation event. Never throws.
    ///     Valid <paramref name="eventName" /> values: <c>memory_embedding_stored</c>,
    ///     <c>memory_embedding_removed</c>, <c>memory_retrieval_executed</c>,
    ///     <c>memory_reconsideration_completed</c>, <c>memory_operation_failed</c>.
    /// </summary>
    /// <param name="protocolId">The protocol this event belongs to.</param>
    /// <param name="eventName">The memory event name (one of the five defined values).</param>
    /// <param name="details">JSON-serialised metadata for the event, or <see langword="null" />.</param>
    /// <param name="error">Error message if the operation failed, or <see langword="null" />.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordMemoryEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? error,
        CancellationToken ct = default);

    /// <summary>
    ///     Records a duplicate-suppression event emitted during final PR comment posting. Never throws.
    ///     Valid <paramref name="eventName" /> values: <c>dedup_summary</c> and <c>dedup_degraded_mode</c>.
    /// </summary>
    /// <param name="protocolId">The protocol this event belongs to.</param>
    /// <param name="eventName">The duplicate-suppression event name.</param>
    /// <param name="details">JSON-serialised metadata for the event, or <see langword="null" />.</param>
    /// <param name="error">Error message if event generation failed, or <see langword="null" />.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordDedupEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? error,
        CancellationToken ct = default);

    /// <summary>
    ///     Records a comment-relevance filter event on the existing protocol path. Never throws.
    ///     Valid <paramref name="eventName" /> values are defined by the Reviewing module's
    ///     comment-relevance protocol event family.
    /// </summary>
    /// <param name="protocolId">The protocol this event belongs to.</param>
    /// <param name="eventName">The comment-relevance event name.</param>
    /// <param name="details">JSON-serialised event metadata, or <see langword="null" />.</param>
    /// <param name="output">JSON-serialised comparable filter output payload, or <see langword="null" />.</param>
    /// <param name="error">Error message if event generation failed, or <see langword="null" />.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordCommentRelevanceEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default);

    /// <summary>
    ///     Records a deterministic review-finding-gate event on the existing protocol path. Never throws.
    /// </summary>
    /// <param name="protocolId">The protocol this event belongs to.</param>
    /// <param name="eventName">The final-gate event name.</param>
    /// <param name="details">JSON-serialised event metadata, or <see langword="null" />.</param>
    /// <param name="output">JSON-serialised final-gate payload, or <see langword="null" />.</param>
    /// <param name="error">Error message if event generation failed, or <see langword="null" />.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordReviewFindingGateEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default);

    /// <summary>
    ///     Records a verification workflow event on the existing protocol path. Never throws.
    /// </summary>
    Task RecordVerificationEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default);

    /// <summary>
    ///     Records review-strategy selection, dispatch, and comparison events on the existing protocol path. Never throws.
    /// </summary>
    Task RecordReviewStrategyEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default);

    /// <summary>
    ///     Records PR-wide staged review events and stage metrics on the existing protocol path. Never throws.
    /// </summary>
    Task RecordPrWideStageEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default);

    /// <summary>
    ///     Records ProRV prefilter stage lifecycle and outcome events on the existing protocol path. Never throws.
    /// </summary>
    Task RecordProRvEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default);
}
