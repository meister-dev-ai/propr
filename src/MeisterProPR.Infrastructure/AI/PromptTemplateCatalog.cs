// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Infrastructure.AI;

internal static class PromptTemplateCatalog
{
    private static readonly IReadOnlyDictionary<string, PromptTemplateDescriptor> Descriptors =
        new Dictionary<string, PromptTemplateDescriptor>(StringComparer.Ordinal)
        {
            [PromptStageKeys.GlobalSystem] = new(PromptStageKeys.GlobalSystem, PromptStageRole.System, "shared/global-system.hbs"),
            ["legacy_pr_review_user"] = new("legacy_pr_review_user", PromptStageRole.User, "shared/legacy-pr-review-user.hbs"),
            ["quality_filter_system"] = new("quality_filter_system", PromptStageRole.System, "shared/quality-filter-system.hbs"),
            ["quality_filter_user"] = new("quality_filter_user", PromptStageRole.User, "shared/quality-filter-user.hbs"),
            ["memory_reconsideration_system"] = new(
                "memory_reconsideration_system",
                PromptStageRole.System,
                "shared/memory-reconsideration-system.hbs"),
            ["memory_reconsideration_user"] = new(
                "memory_reconsideration_user",
                PromptStageRole.User,
                "shared/memory-reconsideration-user.hbs"),
            [PromptStageKeys.PerFileContextSystem] = new(
                PromptStageKeys.PerFileContextSystem, PromptStageRole.System, "file-by-file/per-file-context-system.hbs"),
            [PromptStageKeys.PerFileSecurityLensContextSystem] = new(
                PromptStageKeys.PerFileSecurityLensContextSystem,
                PromptStageRole.System,
                "file-by-file/per-file-security-lens-context-system.hbs"),
            [PromptStageKeys.PerFileUser] = new(PromptStageKeys.PerFileUser, PromptStageRole.User, "file-by-file/per-file-user.hbs"),
            [PromptStageKeys.AgenticFilePlanningSystem] = new(
                PromptStageKeys.AgenticFilePlanningSystem,
                PromptStageRole.System,
                "agentic-file-by-file/planning-system.hbs"),
            [PromptStageKeys.AgenticFilePlanningUser] = new(
                PromptStageKeys.AgenticFilePlanningUser,
                PromptStageRole.User,
                "agentic-file-by-file/planning-user.hbs"),
            [PromptStageKeys.AgenticFileInvestigationSystem] = new(
                PromptStageKeys.AgenticFileInvestigationSystem,
                PromptStageRole.System,
                "agentic-file-by-file/investigation-system.hbs"),
            [PromptStageKeys.AgenticFileInvestigationUser] = new(
                PromptStageKeys.AgenticFileInvestigationUser,
                PromptStageRole.User,
                "agentic-file-by-file/investigation-user.hbs"),
            [PromptStageKeys.SynthesisSystem] = new(PromptStageKeys.SynthesisSystem, PromptStageRole.System, "file-by-file/synthesis-system.hbs"),
            [PromptStageKeys.SynthesisUser] = new(PromptStageKeys.SynthesisUser, PromptStageRole.User, "file-by-file/synthesis-user.hbs"),
            [PromptStageKeys.PrVerificationSystem] = new(PromptStageKeys.PrVerificationSystem, PromptStageRole.System, "shared/pr-verification-system.hbs"),
            [PromptStageKeys.PrVerificationUser] = new(PromptStageKeys.PrVerificationUser, PromptStageRole.User, "shared/pr-verification-user.hbs"),
            ["pr_wide_planning_system"] = new("pr_wide_planning_system", PromptStageRole.System, "pr-wide-agentic/planning-system.hbs"),
            ["pr_wide_planning_user"] = new("pr_wide_planning_user", PromptStageRole.User, "pr-wide-agentic/planning-user.hbs"),
            ["pr_wide_investigation_system"] = new(
                "pr_wide_investigation_system",
                PromptStageRole.System,
                "pr-wide-agentic/investigation-system.hbs"),
            ["pr_wide_investigation_user"] = new(
                "pr_wide_investigation_user",
                PromptStageRole.User,
                "pr-wide-agentic/investigation-user.hbs"),
            ["pr_wide_synthesis_system"] = new("pr_wide_synthesis_system", PromptStageRole.System, "pr-wide-agentic/synthesis-system.hbs"),
            ["pr_wide_synthesis_user"] = new("pr_wide_synthesis_user", PromptStageRole.User, "pr-wide-agentic/synthesis-user.hbs"),
            ["importance_ranking_system"] = new("importance_ranking_system", PromptStageRole.System, "shared/importance-ranking-system.hbs"),
            ["importance_ranking_user"] = new("importance_ranking_user", PromptStageRole.User, "shared/importance-ranking-user.hbs"),
        };

    internal static PromptTemplateDescriptor Get(string stageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageKey);

        if (!Descriptors.TryGetValue(stageKey, out var descriptor))
        {
            throw new InvalidOperationException($"No prompt template mapping exists for stage '{stageKey}'.");
        }

        return descriptor;
    }

    internal sealed record PromptTemplateDescriptor(string StageKey, PromptStageRole PromptRole, string TemplateRelativePath);
}
