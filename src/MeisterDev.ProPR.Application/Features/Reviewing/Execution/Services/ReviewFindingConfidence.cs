// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Resolves correctness confidence from independent evidence without changing impact, likelihood or severity.</summary>
public static class ReviewFindingConfidence
{
    /// <summary>Server-owned provenance for fresh confidence decisions.</summary>
    public const string VerificationSource = "evidence_verification";

    /// <summary>Clears provisional producer confidence when a fresh review skips verification.</summary>
    public static ReviewResult ClearUnverified(ReviewResult result) => result with
    {
        Comments = result.Comments.Select(comment => comment with { RiskAssessment = Resolve(comment.RiskAssessment, []) }).ToList(),
        WithheldFindings = result.WithheldFindings.Select(finding => finding with
        {
            Comment = finding.Comment with { RiskAssessment = Resolve(finding.Comment.RiskAssessment, []) }
        }).ToList(),
    };

    /// <summary>Aggregates every material claim conservatively. Missing metadata affects confidence, not publication.</summary>
    public static FindingRiskAssessment? Resolve(FindingRiskAssessment? risk, IReadOnlyList<VerificationOutcome> outcomes)
    {
        if (risk is null && !outcomes.Any(outcome => outcome.ConfidenceEvidence is not null))
        {
            return null;
        }

        risk ??= new FindingRiskAssessment(FindingImpact.Unknown, FindingLikelihood.Unknown, null, null, null, FindingRiskAssessment.CurrentRubricVersion);

        if (outcomes.Count == 0 && risk.ConfidenceSource == VerificationSource)
        {
            return risk;
        }

        var level = FindingConfidence.Unknown;
        string reason = "Independent verification supplied no complete confidence evidence.";
        if (outcomes.Count > 0 && outcomes.All(IsSupportedOutcome))
        {
            level = outcomes.Min(outcome => SupportedLevel(outcome.ConfidenceEvidence!));
            var uncertainty = outcomes.SelectMany(outcome => outcome.ConfidenceEvidence!.Assumptions)
                .Select(assumption => "Unresolved assumption: " + assumption)
                .Concat(
                    outcomes.Where(outcome => outcome.ConfidenceEvidence!.Level == FindingConfidence.Certain
                                              && !outcome.ConfidenceEvidence.ContradictionCheckPassed)
                        .Select(outcome =>
                            "Certainty is unestablished; separate contradiction check: " + (outcome.ContradictionCheckStatus ?? "not completed") + "."));
            if (ProposedLevel(risk) is { } proposed && proposed < level)
            {
                level = proposed;
                uncertainty = uncertainty.Prepend($"The finding was rated {proposed} before verification, and verification does not raise that level.");
            }

            // The witness and the consequence state why the finding holds, so they open the reason. When the reason is
            // longer than the retained length, the assumptions at its end are left out first.
            reason = WithinReasonLength(
                string.Join(
                    " ",
                    outcomes.Select(outcome =>
                            "Witness: " + outcome.ConfidenceEvidence!.Witness + " Consequence: " + outcome.ConfidenceEvidence.Consequence
                            + " Counterevidence: " + outcome.ConfidenceEvidence.Counterevidence)
                        .Concat(uncertainty)));
        }
        else if (outcomes.FirstOrDefault(outcome => outcome.ContradictionCheckStatus is not null) is { } checkedOutcome)
        {
            reason = "Independent verification supplied no complete confidence evidence. Separate contradiction check: "
                     + checkedOutcome.ContradictionCheckStatus + ".";
        }

        return new FindingRiskAssessment(
            risk.Impact, risk.Likelihood, risk.ImpactReason, risk.LikelihoodReason, risk.Source, risk.RubricVersion,
            level, reason, VerificationSource);
    }

    /// <summary>The confidence a finding carries into verification, which verification can confirm or lower but not raise.</summary>
    public static FindingConfidence? ProposedConfidence(FindingRiskAssessment? risk) => risk is null ? null : ProposedLevel(risk);

    /// <summary>
    ///     The level that <see cref="Resolve" /> publishes for a finding with this one outcome and the proposed confidence
    ///     <paramref name="proposed" />. It is Unknown when the outcome supplies no complete supported evidence.
    /// </summary>
    public static FindingConfidence PublishedLevel(FindingConfidence? proposed, VerificationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!IsSupportedOutcome(outcome))
        {
            return FindingConfidence.Unknown;
        }

        var level = SupportedLevel(outcome.ConfidenceEvidence!);
        return proposed is > FindingConfidence.Unknown and <= FindingConfidence.Certain && proposed < level ? proposed.Value : level;
    }

    /// <summary>Prevents a merged finding from inheriting stronger confidence from one selected source.</summary>
    public static FindingRiskAssessment? Consolidate(FindingRiskAssessment? risk, IEnumerable<FindingRiskAssessment?> sources)
    {
        if (risk is null)
        {
            return null;
        }

        var values = sources.OrderBy(value => value?.Confidence ?? FindingConfidence.Unknown).ToArray();
        var level = values.Select(value => value?.Confidence ?? FindingConfidence.Unknown).DefaultIfEmpty().Min();
        var source = values.All(value => value?.ConfidenceSource == VerificationSource) ? VerificationSource : "consolidated_assessment";
        var reasons = WithinReasonLength(
            string.Join(" ", values.Select(value => value?.ConfidenceReason ?? "One or more source findings have unassessed confidence.").Distinct()));

        return new FindingRiskAssessment(
            risk.Impact, risk.Likelihood, risk.ImpactReason, risk.LikelihoodReason, risk.Source,
            risk.RubricVersion, level, reasons.Length > 0 ? reasons : "One or more source findings have unassessed confidence.", source);
    }

    // The level a finding carries from before this verification: the reviewing model's own proposal, or the weakest
    // level of the findings a merge consolidated. Verification can confirm or lower it, and does not raise it: in
    // adjudicated reviews, findings the producer rated Medium and verification rated High had a false premise more often
    // than findings the producer itself rated High.
    private static FindingConfidence? ProposedLevel(FindingRiskAssessment risk) =>
        risk.ConfidenceSource != VerificationSource && risk.Confidence is > FindingConfidence.Unknown and <= FindingConfidence.Certain
            ? risk.Confidence
            : null;

    /// <summary>
    ///     Keeps a reason within <see cref="FindingRiskAssessment.MaxReasonLength" />. A longer reason ends after its last
    ///     sentence within that length. When no sentence ends in the second half of that length, the reason ends after the
    ///     last word in that half and an ellipsis follows. A reason without a space in that half is cut within a word.
    /// </summary>
    private static string WithinReasonLength(string reason)
    {
        const int maximum = FindingRiskAssessment.MaxReasonLength;
        if (reason.Length <= maximum)
        {
            return reason;
        }

        for (var end = maximum; end > maximum / 2; end--)
        {
            // A sentence ends with a period, a question mark or an exclamation mark that whitespace follows.
            if (reason[end - 1] is '.' or '?' or '!' && char.IsWhiteSpace(reason[end]))
            {
                return reason[..end];
            }
        }

        // One character stays free for the ellipsis, and the cut does not split a surrogate pair.
        var cut = maximum - 1;
        if (char.IsHighSurrogate(reason[cut - 1]))
        {
            cut--;
        }

        var space = reason.LastIndexOf(' ', cut);
        if (space > maximum / 2)
        {
            cut = space;
        }

        return string.Concat(reason.AsSpan(0, cut).TrimEnd(), "…");
    }

    private static FindingConfidence SupportedLevel(VerificationConfidenceEvidence evidence)
    {
        var level = evidence.Level;
        if (evidence.Assumptions.Count > 0)
        {
            level = (FindingConfidence)Math.Min((int)level, (int)FindingConfidence.Medium);
        }

        return level == FindingConfidence.Certain && !evidence.ContradictionCheckPassed ? FindingConfidence.High : level;
    }

    private static bool IsSupportedOutcome(VerificationOutcome outcome) => !outcome.Degraded
                                                                           && outcome.OutcomeKind == VerificationOutcome.SupportedKind
                                                                           && outcome.RecommendedDisposition == FinalGateDecision.PublishDisposition
                                                                           && outcome.ConfidenceEvidence?.HasCompleteEvidence() == true;
}
