// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Every prompt that produces reviewer-facing prose states the client's configured output language, so the
///     natural language of a review does not vary between the per-file passes, the summary and the memory step.
/// </summary>
public sealed class ReviewPromptsOutputLanguageTests
{
    [Fact]
    public void BuildGlobalSystemPrompt_WithConfiguredLanguage_StatesTheLanguageTag()
    {
        var prompt = ReviewPrompts.BuildGlobalSystemPrompt(ContextWithLanguage("de"));

        Assert.Contains("Output language", prompt, StringComparison.Ordinal);
        Assert.Contains("`de`", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGlobalSystemPrompt_WithoutConfiguredLanguage_StatesNoLanguage()
    {
        var prompt = ReviewPrompts.BuildGlobalSystemPrompt(new ReviewSystemContext(null, [], null));

        Assert.DoesNotContain("Output language", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildGlobalSystemPrompt_WithOverriddenSystemPrompt_StillStatesTheLanguage()
    {
        var context = new ReviewSystemContext(null, [], null)
        {
            OutputLanguage = "fr",
            PromptOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SystemPrompt"] = "Review the change.",
            },
        };

        var prompt = ReviewPrompts.BuildGlobalSystemPrompt(context);

        Assert.Contains("Review the change.", prompt, StringComparison.Ordinal);
        Assert.Contains("`fr`", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSynthesisSystemPrompt_WithConfiguredLanguage_StatesTheLanguageTag()
    {
        var prompt = ReviewPrompts.BuildSynthesisSystemPrompt(ContextWithLanguage("de"));

        Assert.Contains("`de`", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSynthesisSystemPrompt_WithOverriddenPrompt_StillStatesTheLanguage()
    {
        var context = new ReviewSystemContext(null, [], null)
        {
            OutputLanguage = "de",
            PromptOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SynthesisSystemPrompt"] = "Summarise the findings.",
            },
        };

        var prompt = ReviewPrompts.BuildSynthesisSystemPrompt(context);

        Assert.Contains("Summarise the findings.", prompt, StringComparison.Ordinal);
        Assert.Contains("`de`", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMemoryReconsiderationSystemPrompt_WithConfiguredLanguage_StatesTheLanguageTag()
    {
        var prompt = ReviewPrompts.BuildMemoryReconsiderationSystemPrompt(ContextWithLanguage("it"));

        Assert.Contains("`it`", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrWideSynthesisSystemPrompt_WithConfiguredLanguage_StatesTheLanguageTag()
    {
        var prompt = ReviewPrompts.BuildPrWideSynthesisSystemPrompt(ContextWithLanguage("de"));

        Assert.Contains("`de`", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPrWideInvestigationSystemPrompt_WithConfiguredLanguage_StatesTheLanguageTag()
    {
        var prompt = ReviewPrompts.BuildPrWideInvestigationSystemPrompt(ContextWithLanguage("de"));

        Assert.Contains("`de`", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputLanguageDirective_LeavesCodeAndIdentifiersAlone()
    {
        var directive = OutputLanguageDirective.Append(string.Empty, "de");

        Assert.Contains("identifiers", directive, StringComparison.Ordinal);
        Assert.Contains("file paths", directive, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OutputLanguageDirective_WithNoLanguage_ReturnsThePromptUnchanged(string? language)
    {
        var result = OutputLanguageDirective.Append("Base prompt.", language);

        Assert.Equal("Base prompt.", result);
    }

    private static ReviewSystemContext ContextWithLanguage(string language)
    {
        return new ReviewSystemContext(null, [], null) { OutputLanguage = language };
    }
}
