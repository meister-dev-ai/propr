// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Outcome of the per-file complexity-triage decision.
///     <see cref="Tier" /> drives review-model selection (replacing the size-based heuristic);
///     <see cref="SecurityEscalate" /> is an escalate-only security signal consumed by the deeper
///     second-look/escalation pass (never lowers anything); <see cref="Why" /> is a short rationale
///     recorded for the trace.
/// </summary>
/// <param name="Spend">
///     What deciding this cost, or <see langword="null" /> when the heuristic answered and no model was asked.
///     Triage runs before the file has a protocol to bill against, so the caller carries the figures forward
///     and attributes them once one exists; without that the job's own total would omit spend it caused.
/// </param>
public sealed record TriageVerdict(
    FileComplexityTier Tier,
    bool SecurityEscalate,
    string Why,
    TriageSpend? Spend = null);

/// <summary>What one triage call consumed, in the terms the job breakdown records.</summary>
/// <param name="ModelId">The model that answered, as the provider names it.</param>
/// <param name="LogicalModelName">The logical model it resolved through, when it resolved through one.</param>
public sealed record TriageSpend(
    string ModelId,
    string? LogicalModelName,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long CacheWriteTokens,
    long ReasoningTokens);
