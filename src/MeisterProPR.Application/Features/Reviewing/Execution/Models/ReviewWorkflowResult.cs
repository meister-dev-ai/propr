// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Captures the final output and structured protocol evidence of one workflow execution.
/// </summary>
public sealed record ReviewWorkflowResult(
    ReviewJob Job,
    ReviewResult FinalResult,
    IReadOnlyList<ReviewJobProtocolDto> Protocols,
    IReadOnlyList<BoundaryIssueReport>? BoundaryIssues = null,
    IReadOnlyList<MergedCandidateFinding>? MergedCandidateFindings = null)
{
    /// <summary>
    ///     Merged candidate findings for this review workflow result. Returns an empty list if no merged findings are available.
    /// </summary>
    public IReadOnlyList<MergedCandidateFinding> MergedCandidateFindingsOrEmpty => this.MergedCandidateFindings ?? [];
}
