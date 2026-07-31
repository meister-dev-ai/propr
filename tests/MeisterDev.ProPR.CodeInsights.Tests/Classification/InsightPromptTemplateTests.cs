using MeisterDev.ProPR.CodeInsights.Classification.Prompts;

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


namespace MeisterDev.ProPR.CodeInsights.Tests.Classification;

/// <summary>
///     Every insight prompt renders from its template file. A stage key whose file is missing or misnamed only
///     fails at the first classification, on a background path whose failures are swallowed, so it would surface
///     as insights that quietly never get classified.
/// </summary>
public sealed class InsightPromptTemplateTests
{
    [Fact]
    public void FindingTypeSystem_ListsEachSuppliedTypeWithItsDefinition()
    {
        var rendered = InsightPrompts.FindingTypeSystem(
            new InsightPromptModels.FindingTypeSystemModel(
                [new InsightPromptModels.InsightTagModel("logic-error", "Wrong control flow.")],
                hasCustomTags: true,
                [new InsightPromptModels.InsightTagModel("domain-rule", "Violates one of our business rules.")]));

        Assert.Contains("- logic-error: Wrong control flow.", rendered, StringComparison.Ordinal);
        Assert.Contains("- domain-rule: Violates one of our business rules.", rendered, StringComparison.Ordinal);
        Assert.Contains("ADDITIONAL TYPES", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FindingTypeSystem_WithNoCustomTypes_OmitsTheTeamSection()
    {
        var rendered = InsightPrompts.FindingTypeSystem(
            new InsightPromptModels.FindingTypeSystemModel(
                [new InsightPromptModels.InsightTagModel("logic-error", "Wrong control flow.")],
                hasCustomTags: false,
                []));

        // An empty section would invite the model to invent entries for it.
        Assert.DoesNotContain("ADDITIONAL TYPES", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FindingTypeUser_CarriesTheAnchorSeverityAndOriginPass()
    {
        var rendered = InsightPrompts.FindingTypeUser(
            new InsightPromptModels.FindingTypeUserModel("src/Service.cs:42", "Error", "PrWide", "A null check is missing."));

        Assert.Contains("src/Service.cs:42", rendered, StringComparison.Ordinal);
        Assert.Contains("Error", rendered, StringComparison.Ordinal);
        Assert.Contains("PrWide", rendered, StringComparison.Ordinal);
        Assert.Contains("A null check is missing.", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void DisregardedFindingSystem_NamesEveryRejectionReasonTheParserAccepts()
    {
        var rendered = InsightPrompts.DisregardedFindingSystem();

        foreach (var reason in new[] { "wrong", "out_of_scope", "redundant", "design_trade_off", "developer_preference" })
        {
            Assert.Contains(reason, rendered, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DisregardedFindingUser_IncludesTheChangeExcerptOnlyWhenThereIsOne(bool hasExcerpt)
    {
        var rendered = InsightPrompts.DisregardedFindingUser(
            new InsightPromptModels.DisregardedFindingUserModel(
                "src/Service.cs",
                "A null check is missing.",
                "Author: works as intended.",
                hasExcerpt,
                hasExcerpt ? "@@ -1 +1 @@" : string.Empty));

        Assert.Contains("A null check is missing.", rendered, StringComparison.Ordinal);
        Assert.Equal(hasExcerpt, rendered.Contains("Relevant change:", StringComparison.Ordinal));
    }

    [Fact]
    public void HumanMissSystem_AsksTheThreeQuestionsTheParserRequires()
    {
        var rendered = InsightPrompts.HumanMissSystem();

        Assert.Contains("isSubstantive", rendered, StringComparison.Ordinal);
        Assert.Contains("wasActedOn", rendered, StringComparison.Ordinal);
        Assert.Contains("isInScope", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void HumanMissUser_CarriesTheLocationStatusAndDiscussion()
    {
        var rendered = InsightPrompts.HumanMissUser(new InsightPromptModels.HumanMissUserModel("src/Service.cs", "resolved", "Reviewer: this leaks."));

        Assert.Contains("src/Service.cs", rendered, StringComparison.Ordinal);
        Assert.Contains("resolved", rendered, StringComparison.Ordinal);
        Assert.Contains("Reviewer: this leaks.", rendered, StringComparison.Ordinal);
    }
}
