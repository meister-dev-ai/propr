// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Supported prompt experiment stage catalog for the offline harness.
/// </summary>
public static class PromptStageCatalog
{
    private static readonly IReadOnlyDictionary<string, PromptStageDefinition> Definitions =
        new Dictionary<string, PromptStageDefinition>(StringComparer.Ordinal)
        {
            [PromptStageKeys.GlobalSystem] = new(
                PromptStageKeys.GlobalSystem, "Global system prompt", "shared", PromptStageRole.System, "ReviewPrompts.BuildGlobalSystemPrompt"),
            [PromptStageKeys.PerFileContextSystem] = new(
                PromptStageKeys.PerFileContextSystem, "Per-file context system prompt", "file-by-file", PromptStageRole.System,
                "ReviewPrompts.BuildPerFileContextPrompt"),
            [PromptStageKeys.PerFileUser] = new(
                PromptStageKeys.PerFileUser, "Per-file user prompt", "file-by-file", PromptStageRole.User, "ReviewPrompts.BuildPerFileUserMessage"),
            [PromptStageKeys.AgenticFilePlanningSystem] = new(
                PromptStageKeys.AgenticFilePlanningSystem, "Agentic file planning system prompt", "agentic-file-by-file", PromptStageRole.System,
                "ReviewPrompts.BuildAgenticFilePlanningSystemPrompt"),
            [PromptStageKeys.AgenticFilePlanningUser] = new(
                PromptStageKeys.AgenticFilePlanningUser, "Agentic file planning user prompt", "agentic-file-by-file", PromptStageRole.User,
                "ReviewPrompts.BuildAgenticFilePlanningUserMessage"),
            [PromptStageKeys.AgenticFileInvestigationSystem] = new(
                PromptStageKeys.AgenticFileInvestigationSystem, "Agentic file investigation system prompt", "agentic-file-by-file", PromptStageRole.System,
                "ReviewPrompts.BuildAgenticFileInvestigationSystemPrompt"),
            [PromptStageKeys.AgenticFileInvestigationUser] = new(
                PromptStageKeys.AgenticFileInvestigationUser, "Agentic file investigation user prompt", "agentic-file-by-file", PromptStageRole.User,
                "ReviewPrompts.BuildAgenticFileInvestigationUserMessage"),
            [PromptStageKeys.SynthesisSystem] = new(
                PromptStageKeys.SynthesisSystem, "Synthesis system prompt", "shared-downstream", PromptStageRole.System,
                "ReviewPrompts.BuildSynthesisSystemPrompt"),
            [PromptStageKeys.SynthesisUser] = new(
                PromptStageKeys.SynthesisUser, "Synthesis user prompt", "shared-downstream", PromptStageRole.User, "ReviewPrompts.BuildSynthesisUserMessage"),
            [PromptStageKeys.PrVerificationSystem] = new(
                PromptStageKeys.PrVerificationSystem, "PR verification system prompt", "shared-downstream", PromptStageRole.System,
                "ReviewPrompts.BuildPrVerificationSystemPrompt"),
            [PromptStageKeys.PrVerificationUser] = new(
                PromptStageKeys.PrVerificationUser, "PR verification user prompt", "shared-downstream", PromptStageRole.User,
                "ReviewPrompts.BuildPrVerificationUserMessage"),
            ["pr_wide_planning_system"] = new(
                "pr_wide_planning_system", "PR-wide planning system prompt", "pr-wide-agentic", PromptStageRole.System,
                "ReviewPrompts.BuildPrWidePlanningSystemPrompt"),
            ["pr_wide_planning_user"] = new(
                "pr_wide_planning_user", "PR-wide planning user prompt", "pr-wide-agentic", PromptStageRole.User,
                "ReviewPrompts.BuildPrWidePlanningUserMessage"),
            ["pr_wide_investigation_system"] = new(
                "pr_wide_investigation_system", "PR-wide investigation system prompt", "pr-wide-agentic", PromptStageRole.System,
                "ReviewPrompts.BuildPrWideInvestigationSystemPrompt"),
            ["pr_wide_investigation_user"] = new(
                "pr_wide_investigation_user", "PR-wide investigation user prompt", "pr-wide-agentic", PromptStageRole.User,
                "ReviewPrompts.BuildPrWideInvestigationUserMessage"),
            ["pr_wide_synthesis_system"] = new(
                "pr_wide_synthesis_system", "PR-wide synthesis system prompt", "pr-wide-agentic", PromptStageRole.System,
                "ReviewPrompts.BuildPrWideSynthesisSystemPrompt"),
            ["pr_wide_synthesis_user"] = new(
                "pr_wide_synthesis_user", "PR-wide synthesis user prompt", "pr-wide-agentic", PromptStageRole.User,
                "ReviewPrompts.BuildPrWideSynthesisUserMessage"),
        };

    /// <summary>
    ///     Gets all prompt stage definitions in the catalog.
    /// </summary>
    public static IReadOnlyList<PromptStageDefinition> All { get; } = Definitions.Values.ToArray();

    /// <summary>
    ///     Try to get the prompt stage definition for a given stage key. Returns true if a definition is found for the specified stage key, false otherwise.
    /// </summary>
    /// <param name="stageKey">The key of the stage for which to retrieve the definition.</param>
    /// <param name="definition">When this method returns, contains the prompt stage definition if found; otherwise, null.</param>
    /// <returns>True if a definition is found for the specified stage key; otherwise, false.</returns>
    public static bool TryGet(string stageKey, out PromptStageDefinition? definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageKey);
        return Definitions.TryGetValue(stageKey, out definition);
    }
}
