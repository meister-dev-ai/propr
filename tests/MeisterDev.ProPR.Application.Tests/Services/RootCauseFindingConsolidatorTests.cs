// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     A root-cause group becomes one comment at the line of its primary finding. The comment keeps the primary's fix,
///     names the other places in its message and in its related locations, and carries the highest classification and
///     risk of the group. Each group is recorded as one merge.
/// </summary>
public sealed class RootCauseFindingConsolidatorTests
{
    private static readonly ReviewSuggestedFix HelperFix = new(12, 12, "var key = id;", "var key = $\"{tenant}:{id}\";");

    [Fact]
    public void Consolidate_KeepsTheAnchorAndFixOfThePrimaryAndListsTheOtherPlaces()
    {
        var helper = new ReviewComment("src/Cache/CacheKey.cs", 12, CommentSeverity.Warning, "The cache key omits the tenant id.")
        {
            SuggestedFix = HelperFix,
        };
        var orders = new ReviewComment(
            "src/Api/Orders.cs", 40, CommentSeverity.Error,
            "Orders of one tenant are served from the cache entry of another tenant. The key comes from the shared helper.");
        var invoices = new ReviewComment("src/Api/Invoices.cs", 7, CommentSeverity.Warning, "Invoices read cached entries without a tenant check.");
        var unrelated = new ReviewComment("src/Ui/Button.ts", 3, CommentSeverity.Suggestion, "The button label is not translated.");

        var result = RootCauseFindingConsolidator.Consolidate(
            [helper, orders, invoices, unrelated], [new FindingRootCauseGroup([0, 1, 2], 0, "Cache key without tenant id")]);

        Assert.Equal(2, result.Comments.Count);
        var survivor = result.Comments[0];
        Assert.Same(unrelated, result.Comments[1]);
        Assert.Equal("src/Cache/CacheKey.cs", survivor.FilePath);
        Assert.Equal(12, survivor.LineNumber);
        Assert.Equal(HelperFix, survivor.SuggestedFix);
        Assert.Equal(CommentSeverity.Error, survivor.Severity);
        Assert.StartsWith("The cache key omits the tenant id.", survivor.Message);
        Assert.Contains("`src/Api/Orders.cs:40`: Orders of one tenant are served from the cache entry of another tenant.", survivor.Message);
        Assert.DoesNotContain("The key comes from the shared helper.", survivor.Message);
        Assert.Contains("`src/Api/Invoices.cs:7`: Invoices read cached entries without a tenant check.", survivor.Message);
        Assert.Equal(
            [new ReviewCommentLocation("src/Api/Orders.cs", 40), new ReviewCommentLocation("src/Api/Invoices.cs", 7)],
            survivor.RelatedLocations);
        Assert.Equal(3, survivor.RootCauseGroupSize);
    }

    [Fact]
    public void Consolidate_RecordsOneMergeWithTheSurvivorAndTheRemovedComments()
    {
        var helper = new ReviewComment("src/Cache/CacheKey.cs", 12, CommentSeverity.Warning, "The cache key omits the tenant id.");
        var orders = new ReviewComment("src/Api/Orders.cs", 40, CommentSeverity.Warning, "Orders read the cache entry of another tenant.");
        var invoices = new ReviewComment("src/Api/Invoices.cs", 7, CommentSeverity.Warning, "Invoices read the cache entry of another tenant.");

        var result = RootCauseFindingConsolidator.Consolidate(
            [helper, orders, invoices], [new FindingRootCauseGroup([0, 1, 2], 0, "Cache key without tenant id")]);

        var merge = Assert.Single(result.Merges);
        Assert.Equal(FindingDeduplicationResult.RootCauseReason, merge.Reason);
        Assert.Equal("Cache key without tenant id", merge.Cause);
        Assert.Same(Assert.Single(result.Comments), Assert.Single(merge.Kept));
        Assert.Equal(2, merge.Removed.Count);
        Assert.Same(orders, merge.Removed[0]);
        Assert.Same(invoices, merge.Removed[1]);
    }

    [Fact]
    public void Consolidate_CarriesTheRiskOfTheMemberWithTheHighestDerivedRisk()
    {
        var low = Risk(FindingImpact.Low, FindingLikelihood.Possible);
        var high = Risk(FindingImpact.High, FindingLikelihood.Likely);
        var helper = new ReviewComment("src/Cache/CacheKey.cs", 12, CommentSeverity.Warning, "The cache key omits the tenant id.") { RiskAssessment = low };
        var orders = new ReviewComment("src/Api/Orders.cs", 40, CommentSeverity.Warning, "Orders read the cache entry of another tenant.")
        {
            RiskAssessment = high,
        };

        var result = RootCauseFindingConsolidator.Consolidate([helper, orders], [new FindingRootCauseGroup([0, 1], 0, null)]);

        var survivor = Assert.Single(result.Comments);
        Assert.Equal("src/Cache/CacheKey.cs", survivor.FilePath);
        Assert.NotNull(survivor.RiskAssessment);
        Assert.Equal(FindingRiskSeverity.High, survivor.RiskAssessment.Severity);
        Assert.Equal(FindingImpact.High, survivor.RiskAssessment.Impact);
        Assert.Equal(FindingLikelihood.Likely, survivor.RiskAssessment.Likelihood);
    }

    [Fact]
    public void Consolidate_WithoutANamedPrimary_KeepsTheMemberWithTheHighestClassification()
    {
        var caller = new ReviewComment("src/Api/Orders.cs", 40, CommentSeverity.Warning, "Orders read the cache entry of another tenant.");
        var helper = new ReviewComment("src/Cache/CacheKey.cs", 12, CommentSeverity.Error, "The cache key omits the tenant id.");

        var result = RootCauseFindingConsolidator.Consolidate([caller, helper], [new FindingRootCauseGroup([0, 1], null, null)]);

        var survivor = Assert.Single(result.Comments);
        Assert.Equal("src/Cache/CacheKey.cs", survivor.FilePath);
        Assert.Equal([new ReviewCommentLocation("src/Api/Orders.cs", 40)], survivor.RelatedLocations);
    }

    [Fact]
    public void Consolidate_WithoutANamedPrimaryAndEqualRank_PrefersTheMemberThatIsNotCrossFile()
    {
        var crossFile = new ReviewComment("src/Api/Orders.cs", 40, CommentSeverity.Warning, "Every caller reads the cache entry of another tenant.")
        {
            FindingClassification = Classification(CodeInsightFindingLevel.CrossFile),
        };
        var local = new ReviewComment("src/Cache/CacheKey.cs", 12, CommentSeverity.Warning, "The cache key omits the tenant id.")
        {
            FindingClassification = Classification(CodeInsightFindingLevel.Statement),
        };

        var result = RootCauseFindingConsolidator.Consolidate([crossFile, local], [new FindingRootCauseGroup([0, 1], null, null)]);

        Assert.Equal("src/Cache/CacheKey.cs", Assert.Single(result.Comments).FilePath);
    }

    // A primary without a file would move the comment off every line of the group.
    [Fact]
    public void Consolidate_NamedPrimaryWithoutAFile_IsReplacedByAMemberWithALine()
    {
        var pullRequestLevel = new ReviewComment(null, null, CommentSeverity.Warning, "Several callers read the cache entry of another tenant.");
        var helper = new ReviewComment("src/Cache/CacheKey.cs", 12, CommentSeverity.Warning, "The cache key omits the tenant id.");

        var result = RootCauseFindingConsolidator.Consolidate([pullRequestLevel, helper], [new FindingRootCauseGroup([0, 1], 0, null)]);

        var survivor = Assert.Single(result.Comments);
        Assert.Equal("src/Cache/CacheKey.cs", survivor.FilePath);
        Assert.Equal([new ReviewCommentLocation(null, null)], survivor.RelatedLocations);
    }

    [Fact]
    public void Consolidate_GroupThatOverlapsAnEarlierGroupOrIsOutOfRange_MergesNothingForThatGroup()
    {
        var first = new ReviewComment("src/A.cs", 1, CommentSeverity.Warning, "The first defect.");
        var second = new ReviewComment("src/B.cs", 2, CommentSeverity.Warning, "The first defect again.");
        var third = new ReviewComment("src/C.cs", 3, CommentSeverity.Warning, "Another defect.");
        IReadOnlyList<ReviewComment> comments = [first, second, third];

        var result = RootCauseFindingConsolidator.Consolidate(
            comments,
            [
                new FindingRootCauseGroup([0, 1], 0, null),
                new FindingRootCauseGroup([1, 2], 2, null),
                new FindingRootCauseGroup([2, 7], 2, null),
            ]);

        Assert.Single(result.Merges);
        Assert.Equal(2, result.Comments.Count);
        Assert.Same(third, result.Comments[1]);
    }

    [Fact]
    public void Consolidate_WithoutGroups_ReturnsTheInputAndNoMerge()
    {
        IReadOnlyList<ReviewComment> comments =
        [
            new ReviewComment("src/A.cs", 1, CommentSeverity.Warning, "The first defect."),
            new ReviewComment("src/B.cs", 2, CommentSeverity.Warning, "Another defect."),
        ];

        var result = RootCauseFindingConsolidator.Consolidate(comments, []);

        Assert.Same(comments, result.Comments);
        Assert.Empty(result.Merges);
    }

    [Theory]
    [InlineData("The key omits the tenant. Callers share entries.", "The key omits the tenant.")]
    [InlineData("The key omits the tenant\n\n```csharp\nvar key = id;\n```", "The key omits the tenant")]
    [InlineData("Is the tenant part of the key? It is not.", "Is the tenant part of the key?")]
    [InlineData("The value 1.5 is read from config.example.json without a check", "The value 1.5 is read from config.example.json without a check")]
    public void FirstSentence_EndsAtTheFirstSentenceEndOrLineBreak(string message, string expected)
    {
        Assert.Equal(expected, RootCauseFindingConsolidator.FirstSentence(message));
    }

    private static FindingRiskAssessment Risk(FindingImpact impact, FindingLikelihood likelihood) =>
        new(
            impact, likelihood, "The impact is stated in the finding.", "The trigger is stated in the finding.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion);

    private static FindingTypeClassification Classification(CodeInsightFindingLevel level) =>
        new(
            [FindingTypeTaxonomy.LogicError], [], level, CodeInsightFindingQualifier.Incorrect, 0.8, FindingTypeTaxonomy.Version,
            FindingTypeClassification.ReviewSource);
}
