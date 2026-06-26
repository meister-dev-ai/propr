// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Outcome of the per-file complexity-triage decision.
///     <see cref="Tier" /> drives review-model selection (replacing the size-based heuristic);
///     <see cref="SecurityEscalate" /> is an escalate-only security signal consumed by the deeper
///     second-look/escalation pass (never lowers anything); <see cref="Why" /> is a short rationale
///     recorded for the trace.
/// </summary>
public sealed record TriageVerdict(FileComplexityTier Tier, bool SecurityEscalate, string Why);
