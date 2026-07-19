// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;

/// <summary>
///     Finalization check that refuses to publish an ungrounded high-severity finding about code the reviewer
///     could not have seen in the diff. The per-file reviewer works primarily from the changed-lines diff, so the
///     floor targets only ERROR findings anchored OUTSIDE the changed lines (pre-existing or structural code):
///     a covering read of the cited file keeps such a finding as-is; no covering read keeps it at ERROR but
///     annotates it as unverified; a covering read that proves the cited line is absent discards it. Findings on
///     or adjacent to changed lines (grounded in the visible diff), findings whose location could not be
///     classified, non-ERROR findings, already-suppressed findings, and findings without grounding (e.g. PR-wide
///     findings) all pass through untouched. Deterministic and model-free — the guarantee is in the code, not in
///     the model cooperating.
/// </summary>
public sealed class RereadFinalizationCheck : IFindingFinalizationCheck
{
    /// <summary>Stable name of this check, surfaced in protocol observations.</summary>
    public const string CheckName = "reread_before_error";

    /// <summary>Note appended to an ERROR comment that was published without a covering read of the cited file.</summary>
    public const string UnverifiedNote =
        "Unverified: this finding concerns code outside the changed lines, and the reviewer did not open the " +
        "cited file to confirm it against the source. Treat with extra scrutiny.";

    /// <inheritdoc />
    public string Name => CheckName;

    /// <inheritdoc />
    public FinalizationCheckOutcome Evaluate(CandidateReviewFinding finding, FinalGateDecision currentDecision)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(currentDecision);

        // The floor applies only to ERROR findings that would otherwise publish; anything the base gate already
        // held back, any lower severity, and any finding without grounding (PR-wide, synthesized, unknown line)
        // is left exactly as the gate decided.
        if (finding.Severity != CommentSeverity.Error
            || !string.Equals(currentDecision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal)
            || finding.ReadGrounding is not { } grounding)
        {
            return FinalizationCheckOutcome.Unchanged(currentDecision);
        }

        // Target only findings the reviewer could not have grounded in the diff it was shown: those anchored
        // outside the changed lines. Findings on or adjacent to a changed line are visible in the diff context,
        // and findings whose location could not be classified are given the benefit of the doubt.
        if (finding.ScopeRelation != ChangedLineRelation.OutsideChange)
        {
            return FinalizationCheckOutcome.Unchanged(currentDecision);
        }

        return grounding switch
        {
            ReviewCommentReadGrounding.Covered => new FinalizationCheckOutcome(
                WithReasonCode(currentDecision, ReviewFindingGateReasonCodes.ErrorFindingRereadVerified),
                new FinalizationObservation(CheckName, finding.FindingId, "verified", "cited lines were read during the pass")),

            ReviewCommentReadGrounding.NotRead => new FinalizationCheckOutcome(
                WithReasonCodeAndNote(currentDecision, ReviewFindingGateReasonCodes.ErrorFindingRereadUnverified, UnverifiedNote),
                new FinalizationObservation(CheckName, finding.FindingId, "unverified", "no covering read of the cited lines")),

            ReviewCommentReadGrounding.CitedLineMissing => new FinalizationCheckOutcome(
                Discard(currentDecision),
                new FinalizationObservation(CheckName, finding.FindingId, "contradicted", "cited line is absent from the source")),

            _ => FinalizationCheckOutcome.Unchanged(currentDecision),
        };
    }

    private static FinalGateDecision WithReasonCode(FinalGateDecision decision, string reasonCode)
    {
        if (decision.ReasonCodes.Contains(reasonCode, StringComparer.Ordinal))
        {
            return decision;
        }

        return new FinalGateDecision(
            decision.FindingId,
            decision.Disposition,
            [.. decision.ReasonCodes, reasonCode],
            decision.RuleSource,
            decision.BlockedInvariantIds,
            decision.EvidenceSnapshot,
            decision.SummaryText,
            decision.PublicationNote);
    }

    private static FinalGateDecision WithReasonCodeAndNote(FinalGateDecision decision, string reasonCode, string note)
    {
        var reasonCodes = decision.ReasonCodes.Contains(reasonCode, StringComparer.Ordinal)
            ? decision.ReasonCodes
            : [.. decision.ReasonCodes, reasonCode];

        return new FinalGateDecision(
            decision.FindingId,
            decision.Disposition,
            reasonCodes,
            decision.RuleSource,
            decision.BlockedInvariantIds,
            decision.EvidenceSnapshot,
            decision.SummaryText,
            note);
    }

    private static FinalGateDecision Discard(FinalGateDecision decision)
    {
        return new FinalGateDecision(
            decision.FindingId,
            FinalGateDecision.DropDisposition,
            [ReviewFindingGateReasonCodes.ErrorFindingRereadContradicted],
            "reread_contradicted_rules",
            decision.BlockedInvariantIds,
            decision.EvidenceSnapshot,
            null);
    }
}
