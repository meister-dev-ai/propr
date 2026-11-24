// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
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

    [Fact]
    public void Apply_WithARelativeReviewLink_ReportsTheCountsWithoutALink()
    {
        var result = BuildResult(Comment("a.cs", 10, CommentSeverity.Info, ReviewCommentScopeRelation.OnChangedLine));

        var applied = ReviewPublicationPolicy.Apply(
            result,
            CommentSeverity.Warning,
            false,
            new Uri("/jobs/0f9b/protocol", UriKind.Relative));

        // Reading AbsoluteUri off a relative Uri throws, which would turn a link nobody can follow into a failed
        // publication. The counts are the part that matters and they still go out.
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

    [Fact]
    public void Apply_WithAMinimumRisk_HoldsBackScoredFindingsBelowIt()
    {
        var high = Comment("a.cs", 10, CommentSeverity.Warning, null) with { RiskAssessment = Risk(FindingImpact.High, FindingLikelihood.Likely) };
        var medium = Comment("b.cs", 20, CommentSeverity.Warning, null) with { RiskAssessment = Risk(FindingImpact.Moderate, FindingLikelihood.Likely) };
        var low = Comment("c.cs", 30, CommentSeverity.Error, null) with { RiskAssessment = Risk(FindingImpact.Low, FindingLikelihood.Likely) };
        var result = BuildResult(high, medium, low);

        var applied = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(MinimumRiskSeverity: FindingRiskSeverity.High), ReviewLink);

        Assert.Equal([high], applied.PublishResult.Comments);
        Assert.Equal(2, applied.WithheldBelowMinimumRisk);
        Assert.Contains("2 below the minimum risk to post", applied.PublishResult.Summary, StringComparison.Ordinal);
    }

    // An unscored finding is not evidence of a low risk, so a risk threshold never holds it back.
    [Fact]
    public void Apply_WithAMinimumRisk_PostsFindingsWithoutARiskAssessment()
    {
        var unscored = Comment("a.cs", 10, CommentSeverity.Warning, null);
        var incomplete = Comment("b.cs", 20, CommentSeverity.Warning, null) with
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Low, FindingLikelihood.Likely, null, null, "review_model", FindingRiskAssessment.CurrentRubricVersion),
        };

        var applied = ReviewPublicationPolicy.Apply(
            BuildResult(unscored, incomplete), new ReviewPublicationSettings(MinimumRiskSeverity: FindingRiskSeverity.Critical), ReviewLink);

        Assert.Equal([unscored, incomplete], applied.PublishResult.Comments);
        Assert.False(applied.AnythingWithheld);
    }

    [Fact]
    public void Apply_WithReviewOnlyTypes_HoldsBackFindingsWhoseTypesAreAllReviewOnly()
    {
        var naming = Comment("a.cs", 10, CommentSeverity.Suggestion, null) with { FindingClassification = Types(FindingTypeTaxonomy.NamingClarity) };
        var namingAndLogic = Comment("b.cs", 20, CommentSeverity.Warning, null) with
        {
            FindingClassification = Types(FindingTypeTaxonomy.NamingClarity, FindingTypeTaxonomy.LogicError),
        };
        var untyped = Comment("c.cs", 30, CommentSeverity.Warning, null);
        var result = BuildResult(naming, namingAndLogic, untyped);

        var applied = ReviewPublicationPolicy.Apply(
            result,
            new ReviewPublicationSettings(FindingTypeDestinations: Destinations((FindingTypeTaxonomy.NamingClarity, FindingPostDestination.ReviewOnly))),
            ReviewLink);

        Assert.Equal([namingAndLogic, untyped], applied.PublishResult.Comments);
        Assert.Equal(1, applied.WithheldFindingType);
        Assert.Equal(0, applied.ListedInSummary);
        Assert.Contains("1 of finding types that are not posted", applied.PublishResult.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Findings listed here and not posted as comments", applied.PublishResult.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WithTheDefaultDestinations_ListsARequestForAMissingTestInTheSummary()
    {
        var missingTest = Comment("src/Parser.cs", 42, CommentSeverity.Suggestion, null)
                .WithMessage("The new branch for empty input has no test. A test should cover it.") with
            {
                FindingClassification = Classified(CodeInsightFindingQualifier.Missing, FindingTypeTaxonomy.DocumentationTests),
            };
        var result = BuildResult(missingTest);

        var applied = ReviewPublicationPolicy.Apply(
            result, new ReviewPublicationSettings(FindingTypeDestinations: FindingPostDestinationMap.Default), ReviewLink);

        Assert.Empty(applied.PublishResult.Comments);
        Assert.Equal(1, applied.ListedInSummary);
        Assert.Equal(0, applied.WithheldTotal);
        var summary = applied.PublishResult.Summary;
        Assert.StartsWith("Reviewed the change.", summary, StringComparison.Ordinal);
        Assert.Contains("**Findings listed here and not posted as comments**", summary, StringComparison.Ordinal);
        Assert.Contains(
            "- `src/Parser.cs:42` Documentation and tests: The new branch for empty input has no test.\n",
            summary.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("A test should cover it.", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("held back from this pull request", summary, StringComparison.Ordinal);
        Assert.Contains($"[Open the full review in ProPR]({ReviewLink.AbsoluteUri})", summary, StringComparison.Ordinal);
    }

    // Only a request for a missing test or document that carries no other type leaves the line. A test request about a
    // security behaviour, a wrong test, and a finding whose qualifier is unknown stay comments.
    [Theory]
    [InlineData(CodeInsightFindingQualifier.Missing, FindingTypeTaxonomy.DocumentationTests, FindingTypeTaxonomy.Security)]
    [InlineData(CodeInsightFindingQualifier.Incorrect, FindingTypeTaxonomy.DocumentationTests)]
    [InlineData(null, FindingTypeTaxonomy.DocumentationTests)]
    public void Apply_WithTheDefaultDestinations_PostsOtherDocumentationAndTestFindingsAsComments(
        CodeInsightFindingQualifier? qualifier,
        params string[] coreSlugs)
    {
        var result = BuildResult(Comment("src/Parser.cs", 42, CommentSeverity.Warning, null) with { FindingClassification = Classified(qualifier, coreSlugs) });

        var applied = ReviewPublicationPolicy.Apply(
            result, new ReviewPublicationSettings(FindingTypeDestinations: FindingPostDestinationMap.Default), ReviewLink);

        Assert.Same(result, applied.PublishResult);
        Assert.Equal(0, applied.ListedInSummary);
    }

    [Fact]
    public void Apply_WithTheDefaultDestinations_PostsAFindingWithoutClassificationAsAComment()
    {
        var result = BuildResult(Comment("src/Parser.cs", 42, CommentSeverity.Warning, null));

        var applied = ReviewPublicationPolicy.Apply(
            result, new ReviewPublicationSettings(FindingTypeDestinations: FindingPostDestinationMap.Default), ReviewLink);

        Assert.Same(result, applied.PublishResult);
        Assert.Equal(0, applied.ListedInSummary);
    }

    [Fact]
    public void Apply_WithAReviewOnlyAndASummaryType_ListsTheFinding()
    {
        var finding = Comment("a.cs", 10, CommentSeverity.Suggestion, null) with
        {
            FindingClassification = Types(FindingTypeTaxonomy.NamingClarity, FindingTypeTaxonomy.DesignStructure),
        };

        var applied = ReviewPublicationPolicy.Apply(
            BuildResult(finding),
            new ReviewPublicationSettings(
                FindingTypeDestinations: Destinations(
                    (FindingTypeTaxonomy.NamingClarity, FindingPostDestination.ReviewOnly),
                    (FindingTypeTaxonomy.DesignStructure, FindingPostDestination.Summary))),
            ReviewLink);

        // The most visible destination among the types of a finding applies, and the summary is more visible than the
        // ProPR review alone. The line names the type that placed the finding in the summary.
        Assert.Empty(applied.PublishResult.Comments);
        Assert.Equal(1, applied.ListedInSummary);
        Assert.Equal(0, applied.WithheldFindingType);
        Assert.Contains("- `a.cs:10` Design and structure: Finding in a.cs", applied.PublishResult.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WithAKeyForTheQualifier_TakesItBeforeTheKeyOfTheType()
    {
        var missing = Comment("a.cs", 10, CommentSeverity.Suggestion, null) with
        {
            FindingClassification = Classified(CodeInsightFindingQualifier.Missing, FindingTypeTaxonomy.DocumentationTests),
        };
        var incorrect = Comment("b.cs", 20, CommentSeverity.Warning, null) with
        {
            FindingClassification = Classified(CodeInsightFindingQualifier.Incorrect, FindingTypeTaxonomy.DocumentationTests),
        };

        var applied = ReviewPublicationPolicy.Apply(
            BuildResult(missing, incorrect),
            new ReviewPublicationSettings(
                FindingTypeDestinations: Destinations(
                    (FindingTypeTaxonomy.DocumentationTests, FindingPostDestination.ReviewOnly),
                    ("documentation-tests:missing", FindingPostDestination.Comment))),
            ReviewLink);

        Assert.Equal([missing], applied.PublishResult.Comments);
        Assert.Equal(1, applied.WithheldFindingType);
        Assert.Equal(0, applied.ListedInSummary);
    }

    [Fact]
    public void Apply_WithOnlyCommentDestinations_ReturnsTheSameResultInstance()
    {
        var result = BuildResult(
            Comment("a.cs", 10, CommentSeverity.Suggestion, null) with
            {
                FindingClassification = Classified(CodeInsightFindingQualifier.Missing, FindingTypeTaxonomy.DocumentationTests),
            });

        var applied = ReviewPublicationPolicy.Apply(
            result,
            new ReviewPublicationSettings(
                FindingTypeDestinations: new Dictionary<string, FindingPostDestination>
                {
                    ["documentation-tests:missing"] = FindingPostDestination.Comment,
                    [FindingTypeTaxonomy.Security] = FindingPostDestination.Comment,
                }),
            ReviewLink);

        Assert.Same(result, applied.PublishResult);
    }

    // The list shows a finding on the pull request, so a finding that another rule holds back is counted under that
    // rule and not listed.
    [Fact]
    public void Apply_WhenASummaryFindingIsOutsideTheChangedLines_CountsItUnderThatRule()
    {
        var outside = Comment("a.cs", 10, CommentSeverity.Suggestion, ReviewCommentScopeRelation.OutsideChange) with
        {
            FindingClassification = Classified(CodeInsightFindingQualifier.Missing, FindingTypeTaxonomy.DocumentationTests),
        };

        var applied = ReviewPublicationPolicy.Apply(
            BuildResult(outside),
            new ReviewPublicationSettings(WithholdOutOfScopeFindings: true, FindingTypeDestinations: FindingPostDestinationMap.Default),
            ReviewLink);

        Assert.Empty(applied.PublishResult.Comments);
        Assert.Equal(0, applied.ListedInSummary);
        Assert.Equal(1, applied.WithheldOutsideChangedLines);
        Assert.DoesNotContain("Findings listed here", applied.PublishResult.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WithMoreThanFifteenSummaryFindings_ListsFifteenAndCountsTheRest()
    {
        var findings = Enumerable.Range(1, 16)
            .Select(index => Comment($"src/File{index}.cs", index, CommentSeverity.Suggestion, null)
                    .WithMessage($"Request number {index} for a test.") with
                {
                    FindingClassification = Classified(CodeInsightFindingQualifier.Missing, FindingTypeTaxonomy.DocumentationTests),
                })
            .ToArray();
        var posted = Comment("src/Posted.cs", 3, CommentSeverity.Error, null);

        var applied = ReviewPublicationPolicy.Apply(
            BuildResult([.. findings, posted]),
            new ReviewPublicationSettings(FindingTypeDestinations: FindingPostDestinationMap.Default),
            ReviewLink);

        var summary = applied.PublishResult.Summary;
        Assert.Equal([posted], applied.PublishResult.Comments);
        Assert.Equal(16, applied.ListedInSummary);
        Assert.Contains("`src/File15.cs:15`", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("`src/File16.cs:16`", summary, StringComparison.Ordinal);
        Assert.Equal(15, summary.Split('\n').Count(line => line.StartsWith("- `src/File", StringComparison.Ordinal)));
        Assert.Contains("1 more finding of this kind is listed in the ProPR review.", summary, StringComparison.Ordinal);
        Assert.True(
            summary.IndexOf("1 more finding of this kind", StringComparison.Ordinal)
            < summary.IndexOf("[Open the full review in ProPR]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Apply_WhenTheSummaryIsBlank_MakesTheListTheWholeBody(string summary)
    {
        var result = new ReviewResult(
            summary,
            [
                Comment("a.cs", 10, CommentSeverity.Suggestion, null) with
                {
                    FindingClassification = Classified(CodeInsightFindingQualifier.Missing, FindingTypeTaxonomy.DocumentationTests),
                },
            ]);

        var applied = ReviewPublicationPolicy.Apply(
            result, new ReviewPublicationSettings(FindingTypeDestinations: FindingPostDestinationMap.Default), ReviewLink);

        var published = applied.PublishResult.Summary;
        Assert.StartsWith("**Findings listed here and not posted as comments**", published, StringComparison.Ordinal);
        Assert.Contains("- `a.cs:10` Documentation and tests: Finding in a.cs", published, StringComparison.Ordinal);
        Assert.Contains($"[Open the full review in ProPR]({ReviewLink.AbsoluteUri})", published, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WhenFindingsAreListedAndHeldBack_ReportsBoth()
    {
        var listed = Comment("a.cs", 10, CommentSeverity.Suggestion, null) with
        {
            FindingClassification = Classified(CodeInsightFindingQualifier.Missing, FindingTypeTaxonomy.DocumentationTests),
        };
        var belowMinimum = Comment("b.cs", 20, CommentSeverity.Info, null);

        var applied = ReviewPublicationPolicy.Apply(
            BuildResult(listed, belowMinimum),
            new ReviewPublicationSettings(CommentSeverity.Suggestion, FindingTypeDestinations: FindingPostDestinationMap.Default),
            ReviewLink);

        var summary = applied.PublishResult.Summary;
        Assert.Equal(1, applied.ListedInSummary);
        Assert.Equal(1, applied.WithheldBelowMinimumSeverity);
        Assert.Contains("- `a.cs:10` Documentation and tests: Finding in a.cs", summary, StringComparison.Ordinal);
        Assert.Contains("**1 finding is held back from this pull request and kept in the ProPR review.**", summary, StringComparison.Ordinal);
        Assert.Contains("- 1 below the minimum severity to post", summary, StringComparison.Ordinal);
    }

    // A finding several rules would hold back is counted once, under the first rule in the order severity, risk,
    // type, scope.
    [Fact]
    public void Apply_CountsAFindingUnderTheFirstRuleThatHoldsItBack()
    {
        var finding = Comment("a.cs", 10, CommentSeverity.Info, ReviewCommentScopeRelation.OutsideChange) with
        {
            RiskAssessment = Risk(FindingImpact.Low, FindingLikelihood.Likely),
            FindingClassification = Types(FindingTypeTaxonomy.NamingClarity),
        };
        var riskAndType = Comment("b.cs", 20, CommentSeverity.Error, null) with
        {
            RiskAssessment = Risk(FindingImpact.Low, FindingLikelihood.Likely),
            FindingClassification = Types(FindingTypeTaxonomy.NamingClarity),
        };

        var applied = ReviewPublicationPolicy.Apply(
            BuildResult(finding, riskAndType),
            new ReviewPublicationSettings(
                CommentSeverity.Warning,
                true,
                FindingRiskSeverity.Medium,
                Destinations((FindingTypeTaxonomy.NamingClarity, FindingPostDestination.ReviewOnly))),
            ReviewLink);

        Assert.Empty(applied.PublishResult.Comments);
        Assert.Equal(1, applied.WithheldBelowMinimumSeverity);
        Assert.Equal(1, applied.WithheldBelowMinimumRisk);
        Assert.Equal(0, applied.WithheldFindingType);
        Assert.Equal(0, applied.WithheldOutsideChangedLines);
        Assert.Equal(2, applied.WithheldTotal);
    }

    [Fact]
    public void Apply_WithDefaultSettings_ReturnsTheSameResultInstance()
    {
        var result = BuildResult(
            Comment("a.cs", 10, CommentSeverity.Info, ReviewCommentScopeRelation.OutsideChange) with
            {
                RiskAssessment = Risk(FindingImpact.Low, FindingLikelihood.Unlikely),
                FindingClassification = Types(FindingTypeTaxonomy.NamingClarity),
            });

        var applied = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(), ReviewLink);

        Assert.Same(result, applied.PublishResult);
    }

    [Fact]
    public void Apply_WithMoreFindingsThanTheLimit_PostsTheHighestRankedInTheirOriginalOrder()
    {
        // Every fifth finding is a suggestion, so the 20 warnings rank above the 5 suggestions.
        var findings = Enumerable.Range(1, 25)
            .Select(index => Comment($"src/File{index}.cs", index, index % 5 == 0 ? CommentSeverity.Suggestion : CommentSeverity.Warning, null))
            .ToArray();
        var warnings = findings.Where(finding => finding.Severity == CommentSeverity.Warning).ToArray();

        var applied = ReviewPublicationPolicy.Apply(BuildResult(findings), new ReviewPublicationSettings(MaximumFindingsToPost: 20), ReviewLink);

        var posted = applied.PublishResult.Comments;
        Assert.Equal(warnings.Length, posted.Count);
        Assert.All(warnings.Zip(posted), pair => Assert.Same(pair.First, pair.Second));
        Assert.Equal(5, applied.WithheldAboveLimit);
        Assert.Equal(5, applied.WithheldTotal);
        var summary = applied.PublishResult.Summary;
        Assert.Contains("**5 findings are held back from this pull request and kept in the ProPR review.**", summary, StringComparison.Ordinal);
        Assert.Contains("- 5 above the limit of 20 findings for one pull request", summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(20)]
    public void Apply_WithNoMoreFindingsThanTheLimit_ReturnsTheSameResultInstance(int count)
    {
        var result = BuildResult(Enumerable.Range(1, count).Select(index => Comment($"src/File{index}.cs", index, CommentSeverity.Warning, null)).ToArray());

        var applied = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(MaximumFindingsToPost: 20), ReviewLink);

        Assert.Same(result, applied.PublishResult);
        Assert.Equal(0, applied.WithheldAboveLimit);
    }

    [Fact]
    public void Apply_WithoutALimit_PostsEveryFinding()
    {
        var result = BuildResult(Enumerable.Range(1, 25).Select(index => Comment($"src/File{index}.cs", index, CommentSeverity.Warning, null)).ToArray());

        var applied = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(MaximumFindingsToPost: null), ReviewLink);

        Assert.Same(result, applied.PublishResult);
        Assert.Equal(0, applied.WithheldAboveLimit);
    }

    [Fact]
    public void Apply_WithTheDefaultSettings_PostsTwentyFindings()
    {
        var result = BuildResult(Enumerable.Range(1, 21).Select(index => Comment($"src/File{index}.cs", index, CommentSeverity.Warning, null)).ToArray());

        var applied = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(), ReviewLink);

        Assert.Equal(FindingPostLimit.Default, applied.PublishResult.Comments.Count);
        Assert.Equal(1, applied.WithheldAboveLimit);
        Assert.Contains("- 1 above the limit of 20 findings for one pull request", applied.PublishResult.Summary, StringComparison.Ordinal);
    }

    // A finding without a risk assessment ranks as a possible occurrence, so a missing rating moves it neither before a
    // likely finding nor behind an unlikely one.
    [Fact]
    public void Apply_WithALimit_RanksAFindingWithoutARiskAssessmentBetweenLikelyAndUnlikelyFindings()
    {
        var unlikely = Comment("a.cs", 10, CommentSeverity.Suggestion, null) with { RiskAssessment = Risk(FindingImpact.Moderate, FindingLikelihood.Unlikely) };
        var unassessed = Comment("b.cs", 20, CommentSeverity.Suggestion, null);
        var likely = Comment("c.cs", 30, CommentSeverity.Suggestion, null) with { RiskAssessment = Risk(FindingImpact.Moderate, FindingLikelihood.Likely) };
        var result = BuildResult(unlikely, unassessed, likely);

        var limitOfTwo = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(MaximumFindingsToPost: 2), ReviewLink);
        var limitOfOne = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(MaximumFindingsToPost: 1), ReviewLink);

        Assert.Equal([unassessed, likely], limitOfTwo.PublishResult.Comments);
        Assert.Equal([likely], limitOfOne.PublishResult.Comments);
        Assert.Contains("- 2 above the limit of 1 finding for one pull request", limitOfOne.PublishResult.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_WithALimit_RanksTheClassificationBeforeTheLikelihood()
    {
        var likelyWarning = Comment("a.cs", 10, CommentSeverity.Warning, null) with { RiskAssessment = Risk(FindingImpact.High, FindingLikelihood.Likely) };
        var unlikelyError = Comment("b.cs", 20, CommentSeverity.Error, null) with { RiskAssessment = Risk(FindingImpact.Low, FindingLikelihood.Unlikely) };

        var applied = ReviewPublicationPolicy.Apply(
            BuildResult(likelyWarning, unlikelyError), new ReviewPublicationSettings(MaximumFindingsToPost: 1), ReviewLink);

        Assert.Equal([unlikelyError], applied.PublishResult.Comments);
    }

    [Fact]
    public void Apply_WithALimit_RanksTheRootCauseGroupSizeBeforeTheRisk()
    {
        var highRisk = Comment("a.cs", 10, CommentSeverity.Warning, null) with { RiskAssessment = Risk(FindingImpact.High, FindingLikelihood.Likely) };
        var group = Comment("b.cs", 20, CommentSeverity.Warning, null) with
        {
            RiskAssessment = Risk(FindingImpact.Low, FindingLikelihood.Likely),
            RootCauseGroupSize = 3,
        };

        var applied = ReviewPublicationPolicy.Apply(BuildResult(highRisk, group), new ReviewPublicationSettings(MaximumFindingsToPost: 1), ReviewLink);

        Assert.Equal([group], applied.PublishResult.Comments);
    }

    [Fact]
    public void Apply_WithALimit_KeepsTheEarlierOfTwoFindingsOfEqualRank()
    {
        var first = Comment("a.cs", 10, CommentSeverity.Warning, null);
        var second = Comment("b.cs", 20, CommentSeverity.Warning, null);

        var applied = ReviewPublicationPolicy.Apply(BuildResult(first, second), new ReviewPublicationSettings(MaximumFindingsToPost: 1), ReviewLink);

        Assert.Same(first, Assert.Single(applied.PublishResult.Comments));
    }

    // The limit is evaluated last. A finding listed in the summary or held back by another rule does not take a place
    // under it.
    [Fact]
    public void Apply_WithALimit_CountsOnlyTheFindingsThatWouldBePostedAsComments()
    {
        var listed = Comment("a.cs", 10, CommentSeverity.Error, null) with
        {
            FindingClassification = Classified(CodeInsightFindingQualifier.Missing, FindingTypeTaxonomy.DocumentationTests),
        };
        var belowMinimum = Comment("b.cs", 20, CommentSeverity.Info, null);
        var posted = Comment("c.cs", 30, CommentSeverity.Warning, null);

        var applied = ReviewPublicationPolicy.Apply(
            BuildResult(listed, belowMinimum, posted),
            new ReviewPublicationSettings(CommentSeverity.Suggestion, FindingTypeDestinations: FindingPostDestinationMap.Default, MaximumFindingsToPost: 1),
            ReviewLink);

        Assert.Equal([posted], applied.PublishResult.Comments);
        Assert.Equal(1, applied.ListedInSummary);
        Assert.Equal(1, applied.WithheldBelowMinimumSeverity);
        Assert.Equal(0, applied.WithheldAboveLimit);
        Assert.DoesNotContain("above the limit", applied.PublishResult.Summary, StringComparison.Ordinal);
    }

    // The pull request already shows a synthesized concern through the comments of the findings it rests on, so the
    // concern is left out. The review record keeps it as a withheld finding with the reason, and the summary does not
    // count it as held back.
    [Fact]
    public void Apply_ASynthesizedConcernWhoseSupportingFindingsArePostedOrListed_IsLeftOutAndKeptInTheRecord()
    {
        var posted = Comment("a.cs", 10, CommentSeverity.Warning, null) with { FindingId = "finding-a" };
        var listed = Comment("b.cs", 20, CommentSeverity.Suggestion, null) with
        {
            FindingId = "finding-b",
            FindingClassification = Classified(CodeInsightFindingQualifier.Missing, FindingTypeTaxonomy.DocumentationTests),
        };
        var concern = Concern("finding-a", "finding-b");
        var result = BuildResult(posted, listed, concern);

        var applied = ReviewPublicationPolicy.Apply(
            result, new ReviewPublicationSettings(FindingTypeDestinations: FindingPostDestinationMap.Default), ReviewLink);

        Assert.Equal([posted], applied.PublishResult.Comments);
        Assert.Equal(1, applied.ListedInSummary);
        Assert.Equal(1, applied.RestatementsNotPosted);
        Assert.False(applied.AnythingWithheld);
        Assert.DoesNotContain("held back", applied.PublishResult.Summary, StringComparison.Ordinal);
        Assert.Equal([posted, listed], applied.RecordResult.Comments);
        var withheld = Assert.Single(applied.RecordResult.WithheldFindings);
        Assert.Same(concern, withheld.Comment);
        Assert.Equal(ReviewFindingGateReasonCodes.RestatesPublishedFindings, withheld.ReasonCode);
        Assert.Equal(3, result.Comments.Count);
    }

    [Fact]
    public void Apply_ASynthesizedConcernWhoseSupportingFindingsArePosted_IsLeftOutWhenNoSettingWithholds()
    {
        var first = Comment("a.cs", 10, CommentSeverity.Warning, null) with { FindingId = "finding-a" };
        var second = Comment("b.cs", 20, CommentSeverity.Warning, null) with { FindingId = "finding-b" };
        var result = BuildResult(first, second, Concern("finding-a", "finding-b"));

        var applied = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(MaximumFindingsToPost: null), ReviewLink);

        Assert.Equal([first, second], applied.PublishResult.Comments);
        Assert.Equal(result.Summary, applied.PublishResult.Summary);
        Assert.Equal(1, applied.RestatementsNotPosted);
    }

    [Fact]
    public void Apply_ASynthesizedConcernWhoseSupportingFindingIsAboveTheLimit_IsPosted()
    {
        var error = Comment("a.cs", 10, CommentSeverity.Error, null) with { FindingId = "finding-a" };
        var suggestion = Comment("b.cs", 20, CommentSeverity.Suggestion, null) with { FindingId = "finding-b" };
        var concern = Concern("finding-a", "finding-b");
        var result = BuildResult(error, suggestion, concern);

        var applied = ReviewPublicationPolicy.Apply(result, new ReviewPublicationSettings(MaximumFindingsToPost: 2), ReviewLink);

        Assert.Equal([error, concern], applied.PublishResult.Comments);
        Assert.Equal(1, applied.WithheldAboveLimit);
        Assert.Equal(0, applied.RestatementsNotPosted);
        Assert.Same(result, applied.RecordResult);
    }

    [Fact]
    public void Apply_ASynthesizedConcernWhoseSupportingFindingIsReviewOnly_IsPosted()
    {
        var posted = Comment("a.cs", 10, CommentSeverity.Warning, null) with { FindingId = "finding-a" };
        var reviewOnly = Comment("b.cs", 20, CommentSeverity.Warning, null) with
        {
            FindingId = "finding-b",
            FindingClassification = Types(FindingTypeTaxonomy.NamingClarity),
        };
        var concern = Concern("finding-a", "finding-b");

        var applied = ReviewPublicationPolicy.Apply(
            BuildResult(posted, reviewOnly, concern),
            new ReviewPublicationSettings(FindingTypeDestinations: Destinations((FindingTypeTaxonomy.NamingClarity, FindingPostDestination.ReviewOnly))),
            ReviewLink);

        Assert.Equal([posted, concern], applied.PublishResult.Comments);
        Assert.Equal(1, applied.WithheldFindingType);
        Assert.Equal(0, applied.RestatementsNotPosted);
    }

    // A concern that is left out frees its place under the limit, and the next finding in the order of the rank takes it.
    [Fact]
    public void Apply_WithALimit_GivesThePlaceOfALeftOutConcernToTheNextFinding()
    {
        var first = Comment("a.cs", 10, CommentSeverity.Error, null) with { FindingId = "finding-a" };
        var second = Comment("b.cs", 20, CommentSeverity.Error, null) with { FindingId = "finding-b" };
        var concern = Concern("finding-a", "finding-b").WithAnchorAndSeverity(null, null, CommentSeverity.Error);
        var unrelated = Comment("c.cs", 30, CommentSeverity.Warning, null);

        var applied = ReviewPublicationPolicy.Apply(
            BuildResult(first, second, concern, unrelated), new ReviewPublicationSettings(MaximumFindingsToPost: 3), ReviewLink);

        Assert.Equal([first, second, unrelated], applied.PublishResult.Comments);
        Assert.Equal(0, applied.WithheldAboveLimit);
        Assert.Equal(1, applied.RestatementsNotPosted);
    }

    // A listed finding that stands for a root-cause group names the other places of the group, as its comment would.
    [Fact]
    public void Apply_ASummaryFindingThatStandsForARootCauseGroup_ListsTheOtherPlaces()
    {
        var classification = Classified(CodeInsightFindingQualifier.Missing, FindingTypeTaxonomy.DocumentationTests);
        var twoPlaces = Comment("a.cs", 10, CommentSeverity.Suggestion, null) with
        {
            FindingClassification = classification,
            RelatedLocations = [new ReviewCommentLocation("b.cs", 20), new ReviewCommentLocation("c.cs", 31)],
        };
        var fivePlaces = Comment("d.cs", 4, CommentSeverity.Suggestion, null) with
        {
            FindingClassification = classification,
            RelatedLocations =
            [
                new ReviewCommentLocation("e.cs", 1), new ReviewCommentLocation("f.cs", null), new ReviewCommentLocation(null, null),
                new ReviewCommentLocation("g.cs", 7), new ReviewCommentLocation("h.cs", 8),
            ],
        };

        var applied = ReviewPublicationPolicy.Apply(
            BuildResult(twoPlaces, fivePlaces), new ReviewPublicationSettings(FindingTypeDestinations: FindingPostDestinationMap.Default), ReviewLink);

        var summary = applied.PublishResult.Summary;
        Assert.Contains("- `a.cs:10` (also at `b.cs:20`, `c.cs:31`) Documentation and tests: Finding in a.cs", summary, StringComparison.Ordinal);
        Assert.Contains(
            "- `d.cs:4` (also at `e.cs:1`, `f.cs`, the pull request and 2 more) Documentation and tests: Finding in d.cs",
            summary,
            StringComparison.Ordinal);
    }

    private static ReviewComment Concern(params string[] supportingFindingIds)
    {
        return new ReviewComment(null, null, CommentSeverity.Warning, "The cache layer has no tenant isolation.")
        {
            SupportingFindingIds = supportingFindingIds,
        };
    }

    private static FindingRiskAssessment Risk(FindingImpact impact, FindingLikelihood likelihood)
    {
        return new FindingRiskAssessment(
            impact, likelihood, "Impact evidence.", "Likelihood evidence.", "review_model", FindingRiskAssessment.CurrentRubricVersion);
    }

    private static FindingTypeClassification Types(params string[] coreSlugs)
    {
        return Classified(null, coreSlugs);
    }

    private static FindingTypeClassification Classified(CodeInsightFindingQualifier? qualifier, params string[] coreSlugs)
    {
        return new FindingTypeClassification(coreSlugs, [], null, qualifier, null, FindingTypeTaxonomy.Version, FindingTypeClassification.ReviewSource);
    }

    private static IReadOnlyDictionary<string, FindingPostDestination> Destinations(params (string Key, FindingPostDestination Destination)[] entries)
    {
        return entries.ToDictionary(entry => entry.Key, entry => entry.Destination, StringComparer.Ordinal);
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
