// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.RegularExpressions;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.CodeInsights.Taxonomy;

namespace MeisterDev.ProPR.CodeInsights.Tests.Taxonomy;

/// <summary>
///     Guards the invariants of the fixed core taxonomy. It is the cross-client comparison axis, so a
///     careless edit here silently re-partitions every trend already computed: these tests are the thing
///     that makes such an edit fail loudly instead.
/// </summary>
public sealed class CodeInsightCoreTaxonomyTests
{
    [Fact]
    public void CoreSlugs_AreUniqueLowerKebabCaseAndNonEmpty()
    {
        var slugs = CodeInsightCoreTaxonomy.All.Select(tag => tag.Slug).ToList();

        Assert.All(slugs, slug => Assert.False(string.IsNullOrWhiteSpace(slug)));
        Assert.All(slugs, slug => Assert.Matches(new Regex("^[a-z0-9]+(-[a-z0-9]+)*$"), slug));
        Assert.Equal(slugs.Count, slugs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(slugs, CodeInsightCoreTaxonomy.AllSlugs);
    }

    [Fact]
    public void EveryCoreTag_CarriesADisplayNameAndADefinitionTheClassifierCanUse()
    {
        Assert.All(
            CodeInsightCoreTaxonomy.All,
            tag =>
            {
                Assert.False(string.IsNullOrWhiteSpace(tag.DisplayName));
                // The definition doubles as the classifier's label description, so an empty or terse one
                // would degrade classification rather than just the UI.
                Assert.False(string.IsNullOrWhiteSpace(tag.Definition));
                Assert.True(tag.Definition.Length > 30, $"'{tag.Slug}' needs a usable definition.");
            });
    }

    [Fact]
    public void EveryCoreTag_MapsToAQualityCharacteristic()
    {
        // The characteristic mapping is what lets a roll-up report on quality characteristics without a
        // second classification call, so no tag may be left unmapped.
        Assert.All(
            CodeInsightCoreTaxonomy.All,
            tag => Assert.True(Enum.IsDefined(tag.Characteristic), $"'{tag.Slug}' has no characteristic."));

        // All four characteristics are represented; a taxonomy that never produces a security or performance
        // finding would answer none of the questions those characteristics exist for.
        var characteristics = CodeInsightCoreTaxonomy.All.Select(tag => tag.Characteristic).Distinct().ToList();
        Assert.Equal(
            Enum.GetValues<CodeInsightQualityCharacteristic>().Length,
            characteristics.Count);
    }

    [Fact]
    public void TheTaxonomy_CoversBothBehaviourChangingAndEvolvabilityFindings()
    {
        // Most of what a reviewer actually raises does not change behaviour. A taxonomy without a resolvable
        // evolvability side would collapse the bulk of findings into one useless bucket.
        Assert.Contains(CodeInsightCoreTaxonomy.All, tag => tag.BehaviourChanging);
        Assert.True(
            CodeInsightCoreTaxonomy.All.Count(tag => !tag.BehaviourChanging) >= 3,
            "The evolvability side needs enough types to be resolvable.");
    }

    [Fact]
    public void TheTaxonomy_HasNoCatchAllType()
    {
        // A misc bucket becomes the landfill for everything uncertain and destroys the comparison axis.
        string[] forbidden = ["other", "misc", "miscellaneous", "unknown", "general"];

        Assert.All(
            CodeInsightCoreTaxonomy.AllSlugs,
            slug => Assert.DoesNotContain(slug, forbidden, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheTaxonomy_StaysSmallEnoughToClassifyReliably()
    {
        // Fewer than eight types stops answering "what kind of finding is this"; many more than a dozen
        // starts failing the classifier-agreement bar the evaluation story measures.
        Assert.InRange(CodeInsightCoreTaxonomy.All.Count, 8, 14);
    }

    [Theory]
    [InlineData("security")]
    [InlineData("SECURITY")]
    [InlineData("  security  ")]
    public void IsCoreSlug_MatchesRegardlessOfCaseAndSurroundingWhitespace(string candidate)
    {
        // Shadow rejection depends on this: a custom tag called "Security" must not slip past the check.
        Assert.True(CodeInsightCoreTaxonomy.IsCoreSlug(candidate));
        Assert.NotNull(CodeInsightCoreTaxonomy.Find(candidate));
    }

    [Theory]
    [InlineData("bespoke-thing")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsCoreSlug_RejectsAnythingOutsideTheCoreSet(string? candidate)
    {
        Assert.False(CodeInsightCoreTaxonomy.IsCoreSlug(candidate));
        Assert.Null(CodeInsightCoreTaxonomy.Find(candidate));
    }

    [Fact]
    public void Version_IsPositive()
    {
        // Assignments stamp this so they stay interpretable against the vocabulary that produced them.
        Assert.True(CodeInsightCoreTaxonomy.Version > 0);
    }

    [Theory]
    [InlineData(CodeInsightQualityCharacteristic.Reliability, CodeInsightConcernClass.Functional)]
    [InlineData(CodeInsightQualityCharacteristic.Security, CodeInsightConcernClass.Functional)]
    [InlineData(CodeInsightQualityCharacteristic.PerformanceEfficiency, CodeInsightConcernClass.Functional)]
    [InlineData(CodeInsightQualityCharacteristic.Maintainability, CodeInsightConcernClass.Evolvability)]
    public void ConcernClassOf_SplitsWhatAUserMeetsFromWhatTheNextDeveloperMeets(
        CodeInsightQualityCharacteristic characteristic,
        CodeInsightConcernClass expected)
    {
        Assert.Equal(expected, CodeInsightCoreTaxonomy.ConcernClassOf(characteristic));
    }

    [Fact]
    public void EveryCoreTypeBelongsToAConcernClass()
    {
        // The class is derived on read, so a type whose characteristic had no class would silently drop out of
        // the grouped distribution rather than failing anywhere visible.
        Assert.All(
            CodeInsightCoreTaxonomy.All,
            definition => Assert.Contains(
                CodeInsightCoreTaxonomy.ConcernClassOf(definition.Characteristic),
                new[] { CodeInsightConcernClass.Functional, CodeInsightConcernClass.Evolvability }));
    }

    [Fact]
    public void AFindingThatIsBothFunctionalAndEvolvability_CountsAsFunctional()
    {
        // Stated rather than left to whichever tag is read first. A logic error that is also hard to read is a
        // logic error, and grouping it under evolvability would hide the more consequential claim.
        var resolved = CodeInsightCoreTaxonomy.ConcernClassOf([CodeInsightCoreTaxonomy.NamingClarity, CodeInsightCoreTaxonomy.LogicError]);

        Assert.Equal(CodeInsightConcernClass.Functional, resolved);
    }

    [Fact]
    public void AFindingWithNoCoreTypeBelongsToNoClass()
    {
        // Unclassified is reported as itself. Assigning a class to a finding that has no type would invent a
        // grouping nobody established.
        Assert.Null(CodeInsightCoreTaxonomy.ConcernClassOf([]));
        Assert.Null(CodeInsightCoreTaxonomy.ConcernClassOf(["bespoke-thing", null, "  "]));
    }

    [Fact]
    public void AnEvolvabilityOnlyFindingStaysEvolvability()
    {
        Assert.Equal(
            CodeInsightConcernClass.Evolvability,
            CodeInsightCoreTaxonomy.ConcernClassOf([CodeInsightCoreTaxonomy.NamingClarity, CodeInsightCoreTaxonomy.DocumentationTests]));
    }
}
