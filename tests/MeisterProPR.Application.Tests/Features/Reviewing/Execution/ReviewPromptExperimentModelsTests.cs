// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class ReviewPromptExperimentModelsTests
{
    [Fact]
    public void PromptExperimentStageCatalog_ContainsSupportedFirstSliceStages()
    {
        var definitions = PromptStageCatalog.All;

        Assert.Contains(definitions, definition => definition.StageKey == PromptStageKeys.GlobalSystem && definition.PromptRole == PromptStageRole.System);
        Assert.Contains(
            definitions, definition => definition.StageKey == PromptStageKeys.PerFileContextSystem && definition.PromptRole == PromptStageRole.System);
        Assert.Contains(definitions, definition => definition.StageKey == PromptStageKeys.PerFileUser && definition.PromptRole == PromptStageRole.User);
        Assert.Contains(
            definitions, definition => definition.StageKey == PromptStageKeys.AgenticFilePlanningSystem && definition.PromptRole == PromptStageRole.System);
        Assert.Contains(
            definitions, definition => definition.StageKey == PromptStageKeys.AgenticFilePlanningUser && definition.PromptRole == PromptStageRole.User);
        Assert.Contains(
            definitions,
            definition => definition.StageKey == PromptStageKeys.AgenticFileInvestigationSystem && definition.PromptRole == PromptStageRole.System);
        Assert.Contains(
            definitions, definition => definition.StageKey == PromptStageKeys.AgenticFileInvestigationUser && definition.PromptRole == PromptStageRole.User);
        Assert.Contains(definitions, definition => definition.StageKey == PromptStageKeys.SynthesisSystem && definition.PromptRole == PromptStageRole.System);
        Assert.Contains(definitions, definition => definition.StageKey == PromptStageKeys.SynthesisUser && definition.PromptRole == PromptStageRole.User);
        Assert.Contains(
            definitions, definition => definition.StageKey == PromptStageKeys.PrVerificationSystem && definition.PromptRole == PromptStageRole.System);
        Assert.Contains(definitions, definition => definition.StageKey == PromptStageKeys.PrVerificationUser && definition.PromptRole == PromptStageRole.User);
        Assert.Contains(definitions, definition => definition.StageKey == "pr_wide_planning_system" && definition.PromptRole == PromptStageRole.System);
        Assert.Contains(definitions, definition => definition.StageKey == "pr_wide_planning_user" && definition.PromptRole == PromptStageRole.User);
        Assert.Contains(definitions, definition => definition.StageKey == "pr_wide_investigation_system" && definition.PromptRole == PromptStageRole.System);
        Assert.Contains(definitions, definition => definition.StageKey == "pr_wide_investigation_user" && definition.PromptRole == PromptStageRole.User);
        Assert.Contains(definitions, definition => definition.StageKey == "pr_wide_synthesis_system" && definition.PromptRole == PromptStageRole.System);
        Assert.Contains(definitions, definition => definition.StageKey == "pr_wide_synthesis_user" && definition.PromptRole == PromptStageRole.User);
    }

    [Fact]
    public void PromptExperimentContext_ExposesTargetedStageKeysInDeclarationOrder()
    {
        var context = new PromptExperimentContext(
            "variant-a",
            [
                new StagePromptVariant(PromptStageKeys.PerFileUser, PromptStageRole.User, PromptCompositionMode.Replace, "shorter file framing"),
                new StagePromptVariant(PromptStageKeys.SynthesisSystem, PromptStageRole.System, PromptCompositionMode.Append, "extra synthesis rule"),
            ]);

        Assert.Equal("variant-a", context.VariantName);
        Assert.Equal([PromptStageKeys.PerFileUser, PromptStageKeys.SynthesisSystem], context.ActiveStageKeys);
        Assert.True(context.TryGetVariant(PromptStageKeys.PerFileUser, PromptStageRole.User, out var variant));
        Assert.NotNull(variant);
        Assert.Equal(PromptCompositionMode.Replace, variant!.CompositionMode);
        Assert.False(context.TryGetVariant(PromptStageKeys.PerFileUser, PromptStageRole.System, out _));
    }

    [Fact]
    public void PromptExperimentEvidence_RequiresAtLeastOnePromptText()
    {
        var ex = Assert.Throws<ArgumentException>(() => new PromptExperimentEvidence(
            PromptStageKeys.PerFileUser,
            "variant-a",
            PromptCompositionMode.Default,
            true,
            null,
            null));

        Assert.Contains("At least one prompt text", ex.Message);
    }
}
