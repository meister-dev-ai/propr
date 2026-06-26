// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Carries the full execution trace for one attempt of a <see cref="ReviewJob" />.
///     Child entity of <see cref="ReviewJob" /> (the aggregate root); accessed only through
///     <c>ReviewJob.Protocol</c>, never queried independently.
/// </summary>
public sealed class ReviewJobProtocol
{
    /// <summary>Unique identifier for this protocol record.</summary>
    public Guid Id { get; init; }

    /// <summary>The review job this protocol belongs to.</summary>
    public Guid JobId { get; init; }

    /// <summary>Attempt ordinal (1-based). Always 1 for the initial attempt.</summary>
    public int AttemptNumber { get; init; }

    /// <summary>
    ///     Human-readable label for this protocol pass (e.g. file path or "synthesis").
    ///     <see langword="null" /> for legacy records.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    ///     The file result this protocol pass belongs to.
    ///     <see langword="null" /> for legacy protocols or synthesis-only passes.
    /// </summary>
    public Guid? FileResultId { get; set; }

    /// <summary>When the protocol was opened (just before the AI review started).</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>When the protocol was closed. <see langword="null" /> while the job is still running.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary><c>"completed"</c>, <c>"failed"</c>, or <c>"cancelled"</c>. <see langword="null" /> while running.</summary>
    public string? Outcome { get; set; }

    /// <summary>
    ///     Sum of input tokens across all <see cref="ProtocolEventKind.AiCall" /> events. <see langword="null" /> when
    ///     unavailable.
    /// </summary>
    public long? TotalInputTokens { get; set; }

    /// <summary>
    ///     Sum of output tokens across all <see cref="ProtocolEventKind.AiCall" /> events. <see langword="null" /> when
    ///     unavailable.
    /// </summary>
    public long? TotalOutputTokens { get; set; }

    /// <summary>
    ///     Sum of cached input tokens across AI calls where the provider exposed cache usage.
    ///     <see langword="null" /> when cache usage was never observable for this protocol pass.
    /// </summary>
    public long? TotalCachedInputTokens { get; set; }

    /// <summary>Cache observability roll-up for this protocol pass.</summary>
    public CacheObservabilityStatus CacheObservability { get; set; } = CacheObservabilityStatus.Unknown;

    /// <summary>Number of agentic loop iterations completed.</summary>
    public int? IterationCount { get; set; }

    /// <summary>Total number of tool invocations made during the loop.</summary>
    public int? ToolCallCount { get; set; }

    /// <summary>Final aggregated confidence score (0–100). <see langword="null" /> when not evaluated.</summary>
    public int? FinalConfidence { get; set; }

    /// <summary>
    ///     The AI connection category (effort tier) used for this protocol pass.
    ///     <see langword="null" /> for legacy records created before per-tier tracking was introduced.
    /// </summary>
    public AiConnectionModelCategory? AiConnectionCategory { get; set; }

    /// <summary>
    ///     The effective AI model deployment name used for this protocol pass (e.g. "gpt-4o").
    ///     <see langword="null" /> for legacy records.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    ///     The kind of review pass this protocol represents — the <c>ReviewPassKind</c> name
    ///     (e.g. <c>"Baseline"</c>, <c>"ProRVAugmentation"</c>). <see langword="null" /> for legacy
    ///     records and passes with no meaningful kind (e.g. synthesis, which the UI derives from <see cref="Label" />).
    /// </summary>
    public string? PassKind { get; set; }

    /// <summary>
    ///     Human-readable reason this pass ran (e.g. "high-risk file — re-reviewed in depth").
    ///     <see langword="null" /> for the baseline pass and legacy records.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>Chronological list of individual steps recorded during the review loop.</summary>
    public ICollection<ProtocolEvent> Events { get; } = [];
}
