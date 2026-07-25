// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>Aggregated execution state used by Reviewing finalization stages.</summary>
public sealed record ReviewFinalizationContext(
    ReviewJob Job,
    PullRequest PullRequest,
    IReadOnlyList<ReviewFileResult> FreshResults,
    IReadOnlyList<string> PerFileSummaries,
    IReadOnlyList<CandidateReviewFinding> CandidateFindings,
    IReadOnlyList<FinalGateDecision> GateDecisions,
    ReviewResult? FinalReviewResult);
