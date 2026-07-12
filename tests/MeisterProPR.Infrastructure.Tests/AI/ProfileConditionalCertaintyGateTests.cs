// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Prompt-render tests for the profile-conditional certainty gate (T111) and
///     the quality-filter posture-aware rules (T115).
///     Also contains the R-2/R-3 sanity guard (T116).
/// </summary>
public sealed class ProfileConditionalCertaintyGateTests
{
    private static ReviewSystemContext ContextWithPosture(ReviewAggressiveness aggressiveness)
    {
        return new ReviewSystemContext(null, [], null)
        {
            Aggressiveness = aggressiveness,
        };
    }

    // T111a — Assertive posture → global system prompt contains emit-with-confidence gate text,
    // does NOT contain "Omission is always preferable to speculation".
    [Fact]
    public void BuildGlobalSystemPrompt_AssertivePosture_ContainsEmitWithConfidenceGate()
    {
        var context = ContextWithPosture(ReviewAggressiveness.Assertive);
        var prompt = ReviewPrompts.BuildGlobalSystemPrompt(context);

        Assert.Contains("A downstream ranking pass decides survival", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Omission is always preferable to speculation", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // T111b — Calm posture → current discard text renders verbatim.
    [Fact]
    public void BuildGlobalSystemPrompt_CalmPosture_ContainsDiscardGate()
    {
        var context = ContextWithPosture(ReviewAggressiveness.Calm);
        var prompt = ReviewPrompts.BuildGlobalSystemPrompt(context);

        Assert.Contains("Omission is always preferable to speculation", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("A downstream ranking pass decides survival", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // T111c — Balanced posture → current discard text renders verbatim.
    [Fact]
    public void BuildGlobalSystemPrompt_BalancedPosture_ContainsDiscardGate()
    {
        var context = ContextWithPosture(ReviewAggressiveness.Balanced);
        var prompt = ReviewPrompts.BuildGlobalSystemPrompt(context);

        Assert.Contains("Omission is always preferable to speculation", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // T111d — null context (no posture) → current discard text renders verbatim.
    [Fact]
    public void BuildGlobalSystemPrompt_NullContext_ContainsDiscardGate()
    {
        var prompt = ReviewPrompts.BuildGlobalSystemPrompt(null);

        Assert.Contains("Omission is always preferable to speculation", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // T115a — Assertive posture quality filter → contains DEMOTE language.
    [Fact]
    public void BuildQualityFilterSystemPrompt_AssertivePosture_ContainsDemoteLanguage()
    {
        var context = ContextWithPosture(ReviewAggressiveness.Assertive);
        var prompt = ReviewPrompts.BuildQualityFilterSystemPrompt(context);

        Assert.Contains("DEMOTE", prompt, StringComparison.OrdinalIgnoreCase);
    }

    // T115b — Calm posture quality filter → byte-identical to baseline (contains DISCARD, not DEMOTE for rule 1).
    [Fact]
    public void BuildQualityFilterSystemPrompt_CalmPosture_ContainsDiscardNotDemote()
    {
        var calmContext = ContextWithPosture(ReviewAggressiveness.Calm);
        var calmPrompt = ReviewPrompts.BuildQualityFilterSystemPrompt(calmContext);

        // Calm posture discards (rather than demotes) speculative comments.
        Assert.Contains("DISCARD any comment whose stance is speculative", calmPrompt, StringComparison.OrdinalIgnoreCase);
    }

    // T115c — Balanced posture quality filter → same as Calm (DISCARD behavior).
    [Fact]
    public void BuildQualityFilterSystemPrompt_BalancedPosture_ContainsDiscardBehavior()
    {
        var balancedContext = ContextWithPosture(ReviewAggressiveness.Balanced);
        var balancedPrompt = ReviewPrompts.BuildQualityFilterSystemPrompt(balancedContext);
        var nullPrompt = ReviewPrompts.BuildQualityFilterSystemPrompt(null);

        // Balanced and null context should produce the same output.
        Assert.Equal(nullPrompt, balancedPrompt);
    }

    // T116 — Sanity guard: Assertive profile has Aggressiveness == Assertive AND
    // contains file-by-file.self-reflection-ranking in PerFileStageIds.
    // The relaxed certainty gate must never ship without the LLM ranker.
    [Fact]
    public void AssertiveProfile_HasSelfReflectionRankingAndAssertiveAggressiveness()
    {
        // The relaxed certainty gate must never ship without the LLM ranker.
        var provider = new ReviewPipelineProfileProvider();
        var profiles = provider.GetProfiles();
        var assertiveProfile = profiles.FirstOrDefault(p =>
            string.Equals(p.ProfileId, ReviewPipelineProfileCatalog.FileByFileAssertiveProfileId, StringComparison.Ordinal));

        Assert.NotNull(assertiveProfile);
        Assert.Equal(ReviewAggressiveness.Assertive, assertiveProfile!.Aggressiveness);
        Assert.Contains(
            FileByFileSelfReflectionRankingStage.StageIdConstant,
            assertiveProfile.PerFileStageIds,
            StringComparer.Ordinal);
    }
}
