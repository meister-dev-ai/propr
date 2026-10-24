// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Services;

public sealed class ReviewFindingConfidenceTests
{
    [Fact]
    public void ClearUnverified_PreservesFindingsAndRiskWhileClearingProvisionalConfidence()
    {
        var comment = new ReviewComment("a.cs", 1, CommentSeverity.Error, "Concrete supported defect.") { RiskAssessment = Risk() };

        var result = ReviewFindingConfidence.ClearUnverified(new ReviewResult("Summary.", [comment]));

        var finding = Assert.Single(result.Comments);
        Assert.Equal(comment.Message, finding.Message);
        Assert.Equal(CommentSeverity.Error, finding.Severity);
        Assert.Equal(FindingRiskSeverity.High, finding.RiskAssessment!.Severity);
        Assert.Equal(FindingConfidence.Unknown, finding.RiskAssessment.Confidence);
    }

    [Theory]
    [InlineData(FindingConfidence.Certain, FindingConfidence.Medium, FindingConfidence.Medium)]
    [InlineData(FindingConfidence.Certain, FindingConfidence.Unknown, FindingConfidence.Unknown)]
    [InlineData(FindingConfidence.Certain, FindingConfidence.High, FindingConfidence.High)]
    public void Resolve_UsesWeakestMaterialClaim(FindingConfidence first, FindingConfidence second, FindingConfidence expected)
    {
        var resolved = ReviewFindingConfidence.Resolve(Risk(), [Outcome(first), Outcome(second)]);

        Assert.Equal(expected, resolved!.Confidence);
        Assert.Equal(FindingRiskSeverity.High, resolved.Severity);
        Assert.Equal("review_model", resolved.Source);
        Assert.Equal(ReviewFindingConfidence.VerificationSource, resolved.ConfidenceSource);
    }

    [Fact]
    public void Resolve_UnresolvedAssumptionsBoundConfidenceAndRemainVisible()
    {
        var outcome = Outcome(FindingConfidence.Certain) with
        {
            ConfidenceEvidence = Outcome(FindingConfidence.Certain).ConfidenceEvidence! with
            {
                Assumptions = ["The external caller permits input zero."]
            }
        };

        var resolved = ReviewFindingConfidence.Resolve(Risk(), [outcome]);

        Assert.Equal(FindingConfidence.Medium, resolved!.Confidence);
        Assert.EndsWith("Unresolved assumption: The external caller permits input zero.", resolved.ConfidenceReason);
    }

    [Fact]
    public void Resolve_StatesTheWitnessAndTheConsequenceBeforeTheCounterevidenceAndTheAssumptions()
    {
        var outcome = Outcome(FindingConfidence.High);
        outcome = outcome with
        {
            ConfidenceEvidence = outcome.ConfidenceEvidence! with { Assumptions = ["The external caller permits input zero."] }
        };

        var resolved = ReviewFindingConfidence.Resolve(Risk(), [outcome]);

        Assert.Equal(
            "Witness: Input zero reaches Write. Consequence: Write deletes the existing entry. "
            + "Counterevidence: No guard excludes input zero. Unresolved assumption: The external caller permits input zero.",
            resolved!.ConfidenceReason);
    }

    // Eight assumptions of this length do not fit into the retained reason together with the evidence. The reason keeps
    // the evidence and the assumptions that fit, and it ends after a complete sentence.
    [Fact]
    public void Resolve_ShortensALongReasonAfterTheLastSentenceThatFits()
    {
        var outcome = Outcome(FindingConfidence.High);
        var assumptions = Enumerable.Range(1, VerificationConfidenceEvidence.MaxAssumptions)
            .Select(number => $"Assumption {number} leaves open which caller reaches the handler, because the judge did not read that caller.")
            .ToArray();
        outcome = outcome with { ConfidenceEvidence = outcome.ConfidenceEvidence! with { Assumptions = assumptions } };

        var reason = ReviewFindingConfidence.Resolve(Risk(), [outcome])!.ConfidenceReason!;

        Assert.True(reason.Length <= FindingRiskAssessment.MaxReasonLength);
        Assert.StartsWith("Witness: Input zero reaches Write. Consequence: Write deletes the existing entry.", reason);
        Assert.EndsWith("because the judge did not read that caller.", reason);
        Assert.Contains("Assumption 7 ", reason);
        Assert.DoesNotContain("Assumption 8 ", reason);
    }

    // A witness without a sentence end that fills the retained length ends after its last complete word.
    [Fact]
    public void Resolve_ShortensALongReasonWithoutASentenceEndAfterItsLastWord()
    {
        var outcome = Outcome(FindingConfidence.High);
        var witness = string.Join(' ', Enumerable.Repeat("unguarded", 102));
        outcome = outcome with { ConfidenceEvidence = outcome.ConfidenceEvidence! with { Witness = witness } };

        var reason = ReviewFindingConfidence.Resolve(Risk(), [outcome])!.ConfidenceReason!;

        Assert.True(reason.Length <= FindingRiskAssessment.MaxReasonLength);
        Assert.StartsWith("Witness: unguarded unguarded", reason);
        Assert.EndsWith(" unguarded…", reason);
    }

    [Fact]
    public void Consolidate_HighestRiskSourceCannotRaiseConfidenceOfOtherClaims()
    {
        var verified = ReviewFindingConfidence.Resolve(Risk(), [Outcome(FindingConfidence.Certain)])!;
        var weaker = ReviewFindingConfidence.Resolve(Risk(), [Outcome(FindingConfidence.Low)])!;

        var combined = ReviewFindingConfidence.Consolidate(verified, [verified, weaker]);

        Assert.Equal(FindingRiskSeverity.High, combined!.Severity);
        Assert.Equal(FindingConfidence.Low, combined.Confidence);
        Assert.Equal(FindingConfidence.Low, ReviewFindingConfidence.Resolve(combined, [])!.Confidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Consolidate_PreservesLimitingEvidenceBeforeTruncatingLongerRationales(bool unassessed)
    {
        var certain = new FindingRiskAssessment(
            FindingImpact.High, FindingLikelihood.Possible, "Data loss.", "Input zero.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, new string('x', FindingRiskAssessment.MaxReasonLength),
            ReviewFindingConfidence.VerificationSource);
        var weaker = unassessed
            ? null
            : new FindingRiskAssessment(
                FindingImpact.Low, FindingLikelihood.Possible, "Limited effect.", "Input zero.",
                "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Low, "The external caller's inputs are unverified.",
                ReviewFindingConfidence.VerificationSource);

        var combined = ReviewFindingConfidence.Consolidate(certain, [certain, weaker]);

        Assert.Equal(unassessed ? FindingConfidence.Unknown : FindingConfidence.Low, combined!.Confidence);
        Assert.StartsWith(unassessed ? "One or more source findings have unassessed confidence." : weaker!.ConfidenceReason, combined.ConfidenceReason);
        Assert.True(combined.ConfidenceReason!.Length <= FindingRiskAssessment.MaxReasonLength);
    }

    [Fact]
    public void Consolidate_ShortensCombinedRationalesAfterTheLastSentenceThatFits()
    {
        var sentences = string.Concat(Enumerable.Repeat("The external caller's inputs are unverified. ", 13)).TrimEnd();
        var weaker = new FindingRiskAssessment(
            FindingImpact.Low, FindingLikelihood.Possible, "Limited effect.", "Input zero.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Low, sentences, ReviewFindingConfidence.VerificationSource);
        var stronger = new FindingRiskAssessment(
            FindingImpact.High, FindingLikelihood.Possible, "Data loss.", "Input zero.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.High, sentences.Replace("external", "internal", StringComparison.Ordinal),
            ReviewFindingConfidence.VerificationSource);

        var combined = ReviewFindingConfidence.Consolidate(stronger, [stronger, weaker])!;

        Assert.True(combined.ConfidenceReason!.Length <= FindingRiskAssessment.MaxReasonLength);
        Assert.StartsWith(sentences, combined.ConfidenceReason);
        Assert.EndsWith("The internal caller's inputs are unverified.", combined.ConfidenceReason);
    }

    [Fact]
    public void Resolve_SuppliesIndependentConfidenceWhenImpactAndLikelihoodAreUnassessed()
    {
        var result = ReviewFindingConfidence.Resolve(null, [Outcome(FindingConfidence.High)]);

        Assert.Equal(FindingConfidence.High, result!.Confidence);
        Assert.Equal(FindingRiskSeverity.Unknown, result.Severity);
        Assert.Null(result.Source);
        Assert.Equal(ReviewFindingConfidence.VerificationSource, result.ConfidenceSource);
    }

    [Theory]
    [InlineData(0, FindingConfidence.Unknown)]
    [InlineData(1024, FindingConfidence.High)]
    [InlineData(1025, FindingConfidence.Unknown)]
    public void Resolve_RequiresEvidenceWithinRetainedTextLimits(int length, FindingConfidence expected)
    {
        var outcome = Outcome(FindingConfidence.High);
        var text = new string('x', length);
        outcome = outcome with
        {
            ConfidenceEvidence = outcome.ConfidenceEvidence! with { Witness = text, Consequence = text, Counterevidence = text }
        };

        var result = ReviewFindingConfidence.Resolve(Risk(), [outcome]);

        Assert.Equal(expected, result!.Confidence);
        Assert.Equal(FindingRiskSeverity.High, result.Severity);
    }

    [Theory]
    [InlineData("witness")]
    [InlineData("consequence")]
    [InlineData("counterevidence")]
    public void Resolve_RejectsWhitespaceEvidenceWithoutChangingSupportedRisk(string field)
    {
        var outcome = Outcome(FindingConfidence.High);
        var evidence = outcome.ConfidenceEvidence!;
        evidence = field switch
        {
            "witness" => evidence with { Witness = " \r\n " },
            "consequence" => evidence with { Consequence = " \r\n " },
            _ => evidence with { Counterevidence = " \r\n " },
        };

        var result = ReviewFindingConfidence.Resolve(Risk(), [outcome with { ConfidenceEvidence = evidence }]);

        Assert.Equal(FindingConfidence.Unknown, result!.Confidence);
        Assert.Equal(FindingRiskSeverity.High, result.Severity);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("oversized")]
    [InlineData("missing")]
    public void Resolve_RejectsIncompleteAssumptions(string scenario)
    {
        var outcome = Outcome(FindingConfidence.High);
        IReadOnlyList<string> assumptions = scenario switch
        {
            "empty" => [string.Empty],
            "whitespace" => [" \t "],
            "oversized" => [new string('x', 1025)],
            _ => null!,
        };

        var result = ReviewFindingConfidence.Resolve(
            Risk(),
            [outcome with { ConfidenceEvidence = outcome.ConfidenceEvidence! with { Assumptions = assumptions } }]);

        Assert.Equal(FindingConfidence.Unknown, result!.Confidence);
        Assert.Equal(FindingRiskSeverity.High, result.Severity);
    }

    [Theory]
    [InlineData(8, FindingConfidence.Medium)]
    [InlineData(9, FindingConfidence.Unknown)]
    public void Resolve_BoundsTheNumberOfUnresolvedAssumptions(int count, FindingConfidence expected)
    {
        var outcome = Outcome(FindingConfidence.High);
        var assumptions = Enumerable.Repeat("An external input remains unverified.", count).ToArray();

        var result = ReviewFindingConfidence.Resolve(
            Risk(),
            [outcome with { ConfidenceEvidence = outcome.ConfidenceEvidence! with { Assumptions = assumptions } }]);

        Assert.Equal(expected, result!.Confidence);
    }

    [Theory]
    [InlineData(FindingConfidence.Medium, FindingConfidence.High, FindingConfidence.Medium, true)]
    [InlineData(FindingConfidence.Low, FindingConfidence.Certain, FindingConfidence.Low, true)]
    [InlineData(FindingConfidence.High, FindingConfidence.Medium, FindingConfidence.Medium, false)]
    [InlineData(FindingConfidence.High, FindingConfidence.High, FindingConfidence.High, false)]
    public void Resolve_DoesNotRaiseTheReviewersOwnConfidence(FindingConfidence proposed, FindingConfidence verified, FindingConfidence expected, bool limited)
    {
        var producer = new FindingRiskAssessment(
            FindingImpact.High, FindingLikelihood.Possible, "Data loss.", "Input zero.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion, proposed, "The reviewing model's own estimate.");

        var resolved = ReviewFindingConfidence.Resolve(producer, [Outcome(verified)]);

        Assert.Equal(expected, resolved!.Confidence);
        Assert.Equal(ReviewFindingConfidence.VerificationSource, resolved.ConfidenceSource);
        Assert.Equal(limited, resolved.ConfidenceReason!.Contains($"The finding was rated {proposed} before verification", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_DoesNotTreatAnEarlierVerificationAsTheReviewersProposal()
    {
        var verifiedEarlier = new FindingRiskAssessment(
            FindingImpact.High, FindingLikelihood.Possible, "Data loss.", "Input zero.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Low, "An earlier verification.", ReviewFindingConfidence.VerificationSource);

        var resolved = ReviewFindingConfidence.Resolve(verifiedEarlier, [Outcome(FindingConfidence.High)]);

        Assert.Equal(FindingConfidence.High, resolved!.Confidence);
    }

    [Theory]
    [InlineData(null, FindingConfidence.High, FindingConfidence.High)]
    [InlineData(FindingConfidence.Medium, FindingConfidence.High, FindingConfidence.Medium)]
    [InlineData(FindingConfidence.High, FindingConfidence.Medium, FindingConfidence.Medium)]
    [InlineData(FindingConfidence.Low, FindingConfidence.Certain, FindingConfidence.Low)]
    [InlineData(FindingConfidence.Certain, FindingConfidence.Certain, FindingConfidence.Certain)]
    [InlineData(FindingConfidence.High, FindingConfidence.Unknown, FindingConfidence.Unknown)]
    public void PublishedLevel_IsTheLevelResolvePublishesForOneOutcome(FindingConfidence? proposed, FindingConfidence verified, FindingConfidence expected)
    {
        var producer = proposed is null
            ? null
            : new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.Possible, "Data loss.", "Input zero.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion, proposed.Value, "The reviewing model's own estimate.");

        var level = ReviewFindingConfidence.PublishedLevel(ReviewFindingConfidence.ProposedConfidence(producer), Outcome(verified));

        Assert.Equal(expected, level);
        Assert.Equal(ReviewFindingConfidence.Resolve(producer, [Outcome(verified)])?.Confidence ?? FindingConfidence.Unknown, level);
    }

    private static FindingRiskAssessment Risk() => new(
        FindingImpact.High, FindingLikelihood.Possible, "Data loss.", "Input zero.", "review_model",
        FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "Generator certainty.");

    private static VerificationOutcome Outcome(FindingConfidence confidence) => new(
        "claim", "finding", VerificationOutcome.SupportedKind,
        FinalGateDecision.PublishDisposition, [], [], VerificationOutcome.StrongEvidence, "Supported.", VerificationOutcome.AiMicroVerifierEvaluator, false)
    {
        ConfidenceEvidence = confidence == FindingConfidence.Unknown
            ? null
            : new VerificationConfidenceEvidence(
                confidence,
                "Input zero reaches Write.", "Write deletes the existing entry.", "No guard excludes input zero.", [], true, true, true),
    };
}
