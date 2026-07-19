// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.ReviewFindingGate;

public sealed class RereadFinalizationCheckTests
{
    private const string FindingId = "finding-pf-001";
    private static readonly RereadFinalizationCheck Sut = new();

    private static CandidateReviewFinding Finding(
        CommentSeverity severity,
        ReviewCommentReadGrounding? grounding,
        ChangedLineRelation? scopeRelation = ChangedLineRelation.OutsideChange)
    {
        return new CandidateReviewFinding(
            FindingId,
            new CandidateFindingProvenance(CandidateFindingProvenance.PerFileCommentOrigin, "per_file_review", "src/Foo.cs"),
            severity,
            "Potential null dereference.",
            CandidateReviewFinding.PerFileCommentCategory,
            "src/Foo.cs",
            12)
        {
            ReadGrounding = grounding,
            ScopeRelation = scopeRelation,
        };
    }

    private static FinalGateDecision Publish()
    {
        return new FinalGateDecision(
            FindingId,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.DefaultPublish],
            "default_publish_rules",
            [],
            null,
            null);
    }

    private static FinalGateDecision SummaryOnly()
    {
        return new FinalGateDecision(
            FindingId,
            FinalGateDecision.SummaryOnlyDisposition,
            [ReviewFindingGateReasonCodes.WeakBroadFinding],
            "broad_finding_rules",
            [],
            null,
            "Broad concern noted.");
    }

    [Fact]
    public void Evaluate_ErrorWithCoveringRead_PublishesUnchangedAndLabelsVerified()
    {
        var outcome = Sut.Evaluate(Finding(CommentSeverity.Error, ReviewCommentReadGrounding.Covered), Publish());

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.Decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.ErrorFindingRereadVerified, outcome.Decision.ReasonCodes);
        Assert.Null(outcome.Decision.PublicationNote);
        Assert.Equal("verified", outcome.Observation?.Outcome);
    }

    [Fact]
    public void Evaluate_ErrorWithNoCoveringRead_KeepsErrorAndAppendsUnverifiedNote()
    {
        var outcome = Sut.Evaluate(Finding(CommentSeverity.Error, ReviewCommentReadGrounding.NotRead), Publish());

        Assert.Equal(FinalGateDecision.PublishDisposition, outcome.Decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.ErrorFindingRereadUnverified, outcome.Decision.ReasonCodes);
        Assert.Equal(RereadFinalizationCheck.UnverifiedNote, outcome.Decision.PublicationNote);
        Assert.Equal("unverified", outcome.Observation?.Outcome);
    }

    [Fact]
    public void Evaluate_ErrorWithAbsentCitedLine_DiscardsFinding()
    {
        var outcome = Sut.Evaluate(Finding(CommentSeverity.Error, ReviewCommentReadGrounding.CitedLineMissing), Publish());

        Assert.Equal(FinalGateDecision.DropDisposition, outcome.Decision.Disposition);
        Assert.Contains(ReviewFindingGateReasonCodes.ErrorFindingRereadContradicted, outcome.Decision.ReasonCodes);
        Assert.Equal("contradicted", outcome.Observation?.Outcome);
    }

    [Theory]
    [InlineData(CommentSeverity.Warning)]
    [InlineData(CommentSeverity.Info)]
    [InlineData(CommentSeverity.Suggestion)]
    public void Evaluate_NonErrorSeverity_IsNeverTouched(CommentSeverity severity)
    {
        var decision = Publish();

        var outcome = Sut.Evaluate(Finding(severity, ReviewCommentReadGrounding.NotRead), decision);

        Assert.Same(decision, outcome.Decision);
        Assert.Null(outcome.Observation);
    }

    [Fact]
    public void Evaluate_ErrorAlreadySuppressedByBaseGate_IsNeverTouched()
    {
        var decision = SummaryOnly();

        var outcome = Sut.Evaluate(Finding(CommentSeverity.Error, ReviewCommentReadGrounding.NotRead), decision);

        Assert.Same(decision, outcome.Decision);
        Assert.Null(outcome.Observation);
    }

    [Fact]
    public void Evaluate_ErrorWithoutGrounding_IsNeverTouched()
    {
        var decision = Publish();

        var outcome = Sut.Evaluate(Finding(CommentSeverity.Error, null), decision);

        Assert.Same(decision, outcome.Decision);
        Assert.Null(outcome.Observation);
    }

    [Theory]
    [InlineData(ChangedLineRelation.OnChangedLine)]
    [InlineData(ChangedLineRelation.AdjacentToChange)]
    [InlineData(null)]
    public void Evaluate_ErrorGroundedInTheVisibleDiff_IsNeverTouched(ChangedLineRelation? scopeRelation)
    {
        // Findings on or adjacent to a changed line are visible in the diff the reviewer was shown, and findings
        // whose location could not be classified get the benefit of the doubt — none are subject to the floor.
        var decision = Publish();

        var outcome = Sut.Evaluate(Finding(CommentSeverity.Error, ReviewCommentReadGrounding.NotRead, scopeRelation), decision);

        Assert.Same(decision, outcome.Decision);
        Assert.Null(outcome.Observation);
    }
}
