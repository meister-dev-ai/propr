// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.AI;

internal static partial class ReviewPrompts
{
    internal static string BuildPrWidePlanningSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("PrWidePlanningSystemPrompt", out var overrideText) == true)
        {
            return ComposePrompt(context, "pr_wide_planning_system", PromptStageRole.System, overrideText!);
        }

        var defaultText = PromptTemplateRuntime.RenderStage("pr_wide_planning_system", new PromptTemplateModels.PrWidePlanningSystemModel());
        return ComposePrompt(context, "pr_wide_planning_system", PromptStageRole.System, defaultText);
    }

    internal static string BuildPrWidePlanningUserMessage(PullRequest pr)
    {
        ArgumentNullException.ThrowIfNull(pr);
        return PromptTemplateRuntime.RenderStage(
            "pr_wide_planning_user",
            new PromptTemplateModels.PrWidePlanningUserModel(
                pr.Title,
                pr.SourceBranch,
                pr.TargetBranch,
                pr.ChangedFiles.Count,
                pr.Description,
                pr.ChangedFiles.Select(file => new PromptTemplateModels.PromptFileManifestItem(file.Path, file.ChangeType.ToString(), false, false)).ToList(),
                pr.ChangedFiles.Select(file =>
                    new PromptTemplateModels.PromptDiffExcerptItem(
                        file.Path,
                        file.IsBinary
                            ? "[binary file omitted]"
                            : ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(file.UnifiedDiff))).ToList()));
    }

    internal static string BuildPrWideInvestigationSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("PrWideInvestigationSystemPrompt", out var overrideText) == true)
        {
            return ComposePrompt(context, "pr_wide_investigation_system", PromptStageRole.System, overrideText!);
        }

        var defaultText = PromptTemplateRuntime.RenderStage("pr_wide_investigation_system", new PromptTemplateModels.PrWideInvestigationSystemModel());
        return ComposePrompt(context, "pr_wide_investigation_system", PromptStageRole.System, defaultText);
    }

    internal static string BuildPrWideInvestigationUserMessage(
        PrWideReviewPlan plan,
        PrWideInvestigationTask task,
        PullRequest pr)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(pr);

        return PromptTemplateRuntime.RenderStage(
            "pr_wide_investigation_user",
            new PromptTemplateModels.PrWideInvestigationUserModel(
                plan.PlanId,
                task.Id,
                task.TaskType,
                task.Concern,
                pr.SourceBranch,
                string.Join(", ", task.AllowedTools),
                task.MaxToolCalls,
                task.SeedFilePaths.Select(path => new PromptTemplateModels.PromptFileManifestItem(path, "context", false, false)).ToList()));
    }

    internal static string BuildPrWideSynthesisSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("PrWideSynthesisSystemPrompt", out var overrideText) == true)
        {
            return ComposePrompt(context, "pr_wide_synthesis_system", PromptStageRole.System, overrideText!);
        }

        var defaultText = PromptTemplateRuntime.RenderStage("pr_wide_synthesis_system", new PromptTemplateModels.PrWideSynthesisSystemModel());
        return ComposePrompt(context, "pr_wide_synthesis_system", PromptStageRole.System, defaultText);
    }

    internal static string BuildPrWideSynthesisUserMessage(
        PrWideReviewPlan plan,
        IReadOnlyList<PrWideInvestigationResult> investigations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(investigations);

        return PromptTemplateRuntime.RenderStage(
            "pr_wide_synthesis_user",
            new PromptTemplateModels.PrWideSynthesisUserModel(
                plan.PlanId,
                plan.Concerns,
                investigations.Select(result => new PromptTemplateModels.PromptInvestigationResultModel(
                    result.TaskId,
                    result.Status,
                    result.Evidence.Count > 0,
                    result.Evidence.Select(evidence => new PromptTemplateModels.PromptSimpleEvidenceItemModel(
                        evidence.Kind,
                        evidence.Summary,
                        evidence.SourceId ?? "no-source")).ToList(),
                    result.CandidateFindings.Count > 0,
                    result.CandidateFindings.Select(finding => new PromptTemplateModels.PromptPrWideCandidateFindingModel(
                        finding.Id,
                        finding.Message,
                        finding.Category,
                        $"{finding.Confidence.Concern}={finding.Confidence.Score}",
                        string.Join(", ", finding.RelatedFilePaths))).ToList())).ToList()));
    }
}
