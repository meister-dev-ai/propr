// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Stable final-gate audit record used by diagnostics and the admin UI.
/// </summary>
public sealed record RecordedFinalGateDecision(
    string FindingId,
    string Disposition,
    string Category,
    CandidateFindingProvenance Provenance,
    EvidenceReference? Evidence,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> BlockedInvariantIds,
    string RuleSource,
    string? SummaryText,
    bool IncludedInFinalSummary = false);
