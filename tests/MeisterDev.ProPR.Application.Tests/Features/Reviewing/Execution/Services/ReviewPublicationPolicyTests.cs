// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution.Services;

/// <summary>
///     Covers the client publication policy: which findings reach the provider, and what the published
///     summary says about the ones that do not.
/// </summary>
public class ReviewPublicationPolicyTests
{
    private static readonly Uri ReviewLink = new("https://propr.example.com/jobs/0f9b/protocol");

    [Fact]
    public void Apply_WhenNothingIsWithheld_ReturnsTheSameResultInstance()
    {
        var result = BuildResult(
            Comment("a.cs", 10, CommentSeverity.Error, ReviewCommentScopeRelation.OnChangedLine),
            Comment("b.cs", 20, CommentSeverity.Info, ReviewCommentScopeRelation.OutsideChange));

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Info, false, ReviewLink);

        // Byte-fidelity for the default configuration: the untouched instance is what proves no footer, no
        // reordering and no copy went out.
        Assert.Same(result, applied.PublishResult);
        Assert.Equal(0, applied.WithheldBelowMinimumSeverity);
        Assert.Equal(0, applied.WithheldOutsideChangedLines);
        Assert.False(applied.AnythingWithheld);
    }

    [Fact]
    public void Apply_WhenWithholdingIsOff_PostsOutsideChangeFindings()
    {
        var outside = Comment("b.cs", 20, CommentSeverity.Warning, ReviewCommentScopeRelation.OutsideChange);
        var result = BuildResult(outside);

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Info, false, ReviewLink);

        Assert.Contains(outside, applied.PublishResult.Comments);
    }

    [Fact]
    public void Apply_WhenWithholdingIsOn_RemovesOnlyOutsideChangeFindings()
    {
        var onChanged = Comment("a.cs", 10, CommentSeverity.Warning, ReviewCommentScopeRelation.OnChangedLine);
        var adjacent = Comment("b.cs", 20, CommentSeverity.Warning, ReviewCommentScopeRelation.AdjacentToChange);
        var unclassified = Comment("c.cs", 30, CommentSeverity.Warning, null);
        var outside = Comment("d.cs", 40, CommentSeverity.Warning, ReviewCommentScopeRelation.OutsideChange);
        var result = BuildResult(onChanged, adjacent, unclassified, outside);

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Info, true, ReviewLink);

        Assert.Equal([onChanged, adjacent, unclassified], applied.PublishResult.Comments);
        Assert.Equal(1, applied.WithheldOutsideChangedLines);
        Assert.Equal(0, applied.WithheldBelowMinimumSeverity);
    }

    [Fact]
    public void Apply_WhenWithholdingIsOn_CountsPublishedFindingsThatCarryNoScopeClassification()
    {
        var result = BuildResult(
            Comment("a.cs", 10, CommentSeverity.Warning, null),
            Comment("b.cs", 20, CommentSeverity.Warning, null),
            Comment("c.cs", 30, CommentSeverity.Warning, ReviewCommentScopeRelation.OnChangedLine));

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Info, true, ReviewLink);

        // Nothing is withheld, which on its own looks the same as a pull request with nothing out of scope.
        // The unclassified count is what separates that from a classifier no longer reaching publication.
        Assert.Equal(3, applied.PublishResult.Comments.Count);
        Assert.False(applied.AnythingWithheld);
        Assert.Equal(2, applied.PublishedWithoutScopeClassification);
    }

    [Fact]
    public void Apply_WhenWithholdingIsOff_DoesNotCountUnclassifiedFindings()
    {
        var result = BuildResult(Comment("a.cs", 10, CommentSeverity.Warning, null));

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Info, false, ReviewLink);

        // The rule is not running, so an unjudged finding says nothing about it.
        Assert.Equal(0, applied.PublishedWithoutScopeClassification);
    }

    [Fact]
    public void Apply_PreservesTheOrderAndIdentityOfSurvivingComments()
    {
        var first = Comment("a.cs", 10, CommentSeverity.Error, ReviewCommentScopeRelation.OnChangedLine);
        var dropped = Comment("b.cs", 20, CommentSeverity.Info, ReviewCommentScopeRelation.OnChangedLine);
        var last = Comment("c.cs", 30, CommentSeverity.Error, ReviewCommentScopeRelation.OnChangedLine);
        var result = BuildResult(first, dropped, last);

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Warning, false, ReviewLink);

        // The posted-ordinal mapping downstream pairs the published and persisted lists by reference, so
        // identity and order have to survive the filter.
        Assert.Collection(
            applied.PublishResult.Comments,
            comment => Assert.Same(first, comment),
            comment => Assert.Same(last, comment));
    }

    [Fact]
    public void Apply_WhenBothRulesMatchOneFinding_CountsItUnderMinimumSeverityOnly()
    {
        var both = Comment("a.cs", 10, CommentSeverity.Suggestion, ReviewCommentScopeRelation.OutsideChange);
        var result = BuildResult(both);

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Warning, true, ReviewLink);

        Assert.Empty(applied.PublishResult.Comments);
        Assert.Equal(1, applied.WithheldBelowMinimumSeverity);
        Assert.Equal(0, applied.WithheldOutsideChangedLines);
    }

    [Fact]
    public void Apply_WhenFindingsAreWithheld_AppendsAFooterReportingEachReason()
    {
        var result = BuildResult(
            Comment("a.cs", 10, CommentSeverity.Info, ReviewCommentScopeRelation.OnChangedLine),
            Comment("b.cs", 20, CommentSeverity.Error, ReviewCommentScopeRelation.OutsideChange));

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Warning, true, ReviewLink);

        var summary = applied.PublishResult.Summary;
        Assert.StartsWith("Reviewed the change.", summary, StringComparison.Ordinal);
        Assert.Contains("2 findings are held back from this pull request", summary, StringComparison.Ordinal);
        Assert.Contains("- 1 below the minimum severity to post", summary, StringComparison.Ordinal);
        Assert.Contains("- 1 in pre-existing code outside this change", summary, StringComparison.Ordinal);
        Assert.Contains($"[Open the full review in ProPR]({ReviewLink.AbsoluteUri})", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WhenOneFindingIsWithheld_UsesSingularWording()
    {
        var result = BuildResult(Comment("a.cs", 10, CommentSeverity.Info, ReviewCommentScopeRelation.OnChangedLine));

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Warning, false, ReviewLink);

        Assert.Contains("1 finding is held back from this pull request", applied.PublishResult.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WhenAReasonContributesNothing_OmitsItsLine()
    {
        var result = BuildResult(Comment("a.cs", 10, CommentSeverity.Error, ReviewCommentScopeRelation.OutsideChange));

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Info, true, ReviewLink);

        Assert.DoesNotContain("below the minimum severity", applied.PublishResult.Summary, StringComparison.Ordinal);
        Assert.Contains("- 1 in pre-existing code outside this change", applied.PublishResult.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WithoutAReviewLink_ReportsTheCountsWithoutALink()
    {
        var result = BuildResult(Comment("a.cs", 10, CommentSeverity.Info, ReviewCommentScopeRelation.OnChangedLine));

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Warning, false, null);

        Assert.Contains("1 finding is held back", applied.PublishResult.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Open the full review in ProPR", applied.PublishResult.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Apply_WhenTheSummaryIsBlank_ReportsTheWithheldFindingsAsTheWholeBody(string summary)
    {
        var result = new ReviewResult(
            summary,
            [Comment("a.cs", 10, CommentSeverity.Info, ReviewCommentScopeRelation.OnChangedLine)]);

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Warning, false, ReviewLink);

        // A blank summary with everything held back would otherwise reach the pull request as an empty review
        // body and no comments, which reads as a review that found nothing. The account of what was held back is
        // the only true statement available, so it stands alone rather than being dropped.
        var published = applied.PublishResult.Summary;
        Assert.StartsWith("**1 finding is held back", published, StringComparison.Ordinal);
        Assert.Contains("- 1 below the minimum severity to post", published, StringComparison.Ordinal);
        Assert.Contains($"[Open the full review in ProPR]({ReviewLink.AbsoluteUri})", published, StringComparison.Ordinal);
        Assert.Equal(1, applied.WithheldBelowMinimumSeverity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Apply_WhenTheSummaryIsBlankAndNothingIsWithheld_LeavesItBlank(string summary)
    {
        var result = new ReviewResult(
            summary,
            [Comment("a.cs", 10, CommentSeverity.Error, ReviewCommentScopeRelation.OnChangedLine)]);

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Warning, false, ReviewLink);

        // Nothing was held back, so there is nothing to say and no body to invent.
        Assert.Same(result, applied.PublishResult);
        Assert.Equal(summary, applied.PublishResult.Summary);
    }

    [Fact]
    public void Apply_WhenEveryFindingIsWithheld_KeepsTheSummaryAndPublishesNoComments()
    {
        var result = BuildResult(
            Comment("a.cs", 10, CommentSeverity.Info, ReviewCommentScopeRelation.OnChangedLine),
            Comment("b.cs", 20, CommentSeverity.Suggestion, ReviewCommentScopeRelation.OnChangedLine));

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Error, false, ReviewLink);

        Assert.Empty(applied.PublishResult.Comments);
        Assert.Contains("2 findings are held back", applied.PublishResult.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_CarriesTheRemainingResultStateOntoThePublishedCopy()
    {
        var result = BuildResult(Comment("a.cs", 10, CommentSeverity.Info, ReviewCommentScopeRelation.OnChangedLine)) with
        {
            CarriedForwardFilePaths = ["carried.cs"],
            CarriedForwardCandidatesSkipped = 3,
            ContextDegradedFilePaths = ["degraded.cs"],
            BudgetSoftCapped = true,
        };

        var applied = ReviewPublicationPolicy.Apply(result, CommentSeverity.Warning, false, ReviewLink);

        // The publish subset is a copy, so anything the adapters read off the result has to come along.
        Assert.Equal(["carried.cs"], applied.PublishResult.CarriedForwardFilePaths);
        Assert.Equal(3, applied.PublishResult.CarriedForwardCandidatesSkipped);
        Assert.Equal(["degraded.cs"], applied.PublishResult.ContextDegradedFilePaths);
        Assert.True(applied.PublishResult.BudgetSoftCapped);
    }

    [Fact]
    public void Apply_DoesNotChangeThePersistedResult()
    {
        var result = BuildResult(
            Comment("a.cs", 10, CommentSeverity.Info, ReviewCommentScopeRelation.OnChangedLine),
            Comment("b.cs", 20, CommentSeverity.Error, ReviewCommentScopeRelation.OutsideChange));

        ReviewPublicationPolicy.Apply(result, CommentSeverity.Warning, true, ReviewLink);

        Assert.Equal(2, result.Comments.Count);
        Assert.Equal("Reviewed the change.", result.Summary);
    }

    private static ReviewResult BuildResult(params ReviewComment[] comments)
    {
        return new ReviewResult("Reviewed the change.", comments);
    }

    private static ReviewComment Comment(
        string path,
        int line,
        CommentSeverity severity,
        ReviewCommentScopeRelation? scopeRelation)
    {
        return new ReviewComment(path, line, severity, $"Finding in {path}")
        {
            ScopeRelation = scopeRelation,
        };
    }
}
