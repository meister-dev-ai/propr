// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Prompt-render tests for the design-review scope flag (<c>designReviewScope</c>), which rides on
///     <see cref="ReviewAggressiveness" />: on for Balanced/Assertive, off for Calm and null context.
///     Covers the three flag-gated edits — the per-file mandate/categories/calibration (per-file context
///     prompt), the broadened certainty-gate item 2, and the widened top-level role line (both in the
///     global system prompt). When off, each prompt keeps its current defect-only framing.
/// </summary>
public sealed class ProfileConditionalDesignReviewScopeTests
{
    private static ReviewSystemContext ContextWithPosture(ReviewAggressiveness aggressiveness)
    {
        return new ReviewSystemContext(null, [], null)
        {
            Aggressiveness = aggressiveness,
        };
    }

    private static string PerFilePrompt(ReviewSystemContext? context)
    {
        return ReviewPrompts.BuildPerFileContextPrompt(context, "src/Foo.cs", 1, 1);
    }

    // ── Per-file mandate + design categories ────────────────────────────────────

    [Theory]
    [InlineData(ReviewAggressiveness.Balanced)]
    [InlineData(ReviewAggressiveness.Assertive)]
    public void PerFileContext_DesignScopeOn_ReframesMandateAndAddsDesignCategories(ReviewAggressiveness posture)
    {
        var prompt = PerFilePrompt(ContextWithPosture(posture));

        // No leading blank line from the standalone {{#if}} block at the top of the template.
        Assert.StartsWith("Your job is to review this change the way a thorough senior human reviewer would", prompt, StringComparison.Ordinal);
        Assert.Contains("substantive design and quality concerns a competent reviewer would raise on this PR", prompt, StringComparison.Ordinal);
        Assert.Contains("API design & ergonomics:", prompt, StringComparison.Ordinal);
        Assert.Contains("Behavioral consistency / least astonishment:", prompt, StringComparison.Ordinal);
        Assert.Contains("Test quality:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Your primary job is to find real defects", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PerFileContext_DesignScopeOff_KeepsDefectOnlyMandate()
    {
        var prompt = PerFilePrompt(ContextWithPosture(ReviewAggressiveness.Calm)).Replace("\r\n", "\n", StringComparison.Ordinal);

        // No leading blank line, and the off-branch mandate opens the prompt verbatim.
        Assert.StartsWith("Your primary job is to find real defects this change introduces", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("API design & ergonomics:", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("substantive design and quality concerns", prompt, StringComparison.Ordinal);
        // The standalone {{#if}} category block leaves no stray blank line: exactly one blank line
        // separates the last (unchanged) defect category from the calibration heading.
        Assert.Contains("(see the contract-change check below).\n\nCalibration — apply asymmetrically:", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PerFileContext_NullContext_KeepsDefectOnlyMandate()
    {
        var prompt = PerFilePrompt(null);

        Assert.Contains("Your primary job is to find real defects", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("API design & ergonomics:", prompt, StringComparison.Ordinal);
    }

    // ── Per-file calibration clause ─────────────────────────────────────────────

    [Fact]
    public void PerFileContext_DesignScopeOn_CalibrationRaisesDesignConcerns()
    {
        var prompt = PerFilePrompt(ContextWithPosture(ReviewAggressiveness.Balanced));

        Assert.Contains("ARE worth raising even when they are not provable runtime defects", prompt, StringComparison.Ordinal);
        // The narrow "prefer saying nothing over guessing" clause is only the off-branch wording.
        Assert.DoesNotContain("prefer saying nothing over guessing", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PerFileContext_DesignScopeOff_CalibrationPrefersSilence()
    {
        var prompt = PerFilePrompt(ContextWithPosture(ReviewAggressiveness.Calm));

        Assert.Contains("prefer saying nothing over guessing", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ARE worth raising even when they are not provable runtime defects", prompt, StringComparison.Ordinal);
    }

    // ── Broadened certainty-gate item 2 (global system prompt) ──────────────────

    [Theory]
    [InlineData(ReviewAggressiveness.Balanced)]
    [InlineData(ReviewAggressiveness.Assertive)]
    public void GlobalSystem_DesignScopeOn_BroadensCertaintyGateItemTwo(ReviewAggressiveness posture)
    {
        var prompt = ReviewPrompts.BuildGlobalSystemPrompt(ContextWithPosture(posture));

        Assert.Contains("substantive design/quality concern a competent reviewer would raise", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Is the defect definite, not suspected?", prompt, StringComparison.Ordinal);
    }

    // The broadened item 2 replaces the defect-only item 2 in place: items 1/2/3 must stay a
    // contiguous numbered list with no blank line leaked by the standalone {{#if}} block.
    [Fact]
    public void GlobalSystem_CertaintyGateItemsStayContiguous()
    {
        var on = ReviewPrompts.BuildGlobalSystemPrompt(ContextWithPosture(ReviewAggressiveness.Balanced)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var off = ReviewPrompts.BuildGlobalSystemPrompt(ContextWithPosture(ReviewAggressiveness.Calm)).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "1. Have I directly seen the problematic code in the diff or via get_file_content?\n2. Is the issue real and specific — either a definite defect, OR a substantive design/quality concern a competent reviewer would raise (not a trivial nitpick)?\n3. Does the comment name a specific line, token, call, or value?",
            on,
            StringComparison.Ordinal);
        Assert.Contains(
            "1. Have I directly seen the problematic code in the diff or via get_file_content?\n2. Is the defect definite, not suspected?\n3. Does the comment name a specific line, token, call, or value?",
            off,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalSystem_DesignScopeOff_KeepsDefectOnlyCertaintyGate()
    {
        var calm = ReviewPrompts.BuildGlobalSystemPrompt(ContextWithPosture(ReviewAggressiveness.Calm));
        var nullContext = ReviewPrompts.BuildGlobalSystemPrompt(null);

        Assert.Contains("Is the defect definite, not suspected?", calm, StringComparison.Ordinal);
        Assert.DoesNotContain("substantive design/quality concern a competent reviewer would raise", calm, StringComparison.Ordinal);
        Assert.Contains("Is the defect definite, not suspected?", nullContext, StringComparison.Ordinal);
    }

    // ── Widened top-level role line (global system prompt) ──────────────────────

    [Theory]
    [InlineData(ReviewAggressiveness.Balanced)]
    [InlineData(ReviewAggressiveness.Assertive)]
    public void GlobalSystem_DesignScopeOn_WidensRoleLine(ReviewAggressiveness posture)
    {
        var prompt = ReviewPrompts.BuildGlobalSystemPrompt(ContextWithPosture(posture));

        Assert.Contains("API design, and review-worthy quality concerns", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalSystem_DesignScopeOff_KeepsBaselineRoleLine()
    {
        var calm = ReviewPrompts.BuildGlobalSystemPrompt(ContextWithPosture(ReviewAggressiveness.Calm));
        var nullContext = ReviewPrompts.BuildGlobalSystemPrompt(null);

        Assert.DoesNotContain("API design, and review-worthy quality concerns", calm, StringComparison.Ordinal);
        Assert.DoesNotContain("API design, and review-worthy quality concerns", nullContext, StringComparison.Ordinal);
        Assert.Contains("performance, and maintainability.", calm, StringComparison.Ordinal);
    }
}
