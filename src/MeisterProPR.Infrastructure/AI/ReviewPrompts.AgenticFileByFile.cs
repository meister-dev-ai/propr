// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.AI;

internal static partial class ReviewPrompts
{
    internal static string BuildAgenticFilePlanningSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("AgenticFilePlanningSystemPrompt", out var overrideText) == true)
        {
            return ComposePrompt(context, PromptStageKeys.AgenticFilePlanningSystem, PromptStageRole.System, overrideText!);
        }

        var defaultText = PromptTemplateRuntime.RenderStage(
            PromptStageKeys.AgenticFilePlanningSystem,
            new PromptTemplateModels.AgenticFilePlanningSystemModel(BuildFocusedReviewGuidanceSection(context?.PerFileHint?.FocusedReviewGuidance)));

        return ComposePrompt(context, PromptStageKeys.AgenticFilePlanningSystem, PromptStageRole.System, defaultText);
    }

    internal static string BuildAgenticFilePlanningUserMessage(ChangedFile file, PullRequest pr, ReviewSystemContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(pr);

        var defaultText = PromptTemplateRuntime.RenderStage(
            PromptStageKeys.AgenticFilePlanningUser,
            new PromptTemplateModels.AgenticFilePlanningUserModel(
                pr.Title,
                pr.SourceBranch,
                pr.TargetBranch,
                file.Path,
                pr.Description,
                pr.ChangedFiles.Select(changedFile => new PromptTemplateModels.PromptFileManifestItem(
                    changedFile.Path,
                    changedFile.ChangeType.ToString(),
                    false,
                    changedFile.Path == file.Path)).ToList(),
                file.IsBinary ? "[binary file omitted]" : file.UnifiedDiff,
                pr.LinkedItems?.Count > 0,
                MapLinkedItems(pr.LinkedItems)));

        return ComposePrompt(context, PromptStageKeys.AgenticFilePlanningUser, PromptStageRole.User, defaultText);
    }

    internal static string BuildAgenticFileInvestigationSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("AgenticFileInvestigationSystemPrompt", out var overrideText) == true)
        {
            return ComposePrompt(context, PromptStageKeys.AgenticFileInvestigationSystem, PromptStageRole.System, overrideText!);
        }

        var defaultText = PromptTemplateRuntime.RenderStage(PromptStageKeys.AgenticFileInvestigationSystem);

        return ComposePrompt(context, PromptStageKeys.AgenticFileInvestigationSystem, PromptStageRole.System, defaultText);
    }

    internal static string BuildAgenticFileInvestigationUserMessage(
        AgenticFileReviewPlan plan,
        AgenticFileInvestigationTask task,
        PullRequest pr,
        ReviewSystemContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(pr);

        var defaultText = PromptTemplateRuntime.RenderStage(
            PromptStageKeys.AgenticFileInvestigationUser,
            new PromptTemplateModels.AgenticFileInvestigationUserModel(
                plan.PlanId,
                plan.AnchorFilePath,
                task.TaskId,
                task.TaskType,
                task.Concern,
                pr.SourceBranch,
                string.Join(", ", task.AllowedTools),
                task.MaxToolCalls,
                task.SeedFilePaths.Select(path => new PromptTemplateModels.PromptFileManifestItem(path, "context", false, path == plan.AnchorFilePath))
                    .ToList()));

        return ComposePrompt(context, PromptStageKeys.AgenticFileInvestigationUser, PromptStageRole.User, defaultText);
    }
}
