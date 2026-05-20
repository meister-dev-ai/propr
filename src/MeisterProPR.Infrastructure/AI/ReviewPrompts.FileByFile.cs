// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.AI;

internal static partial class ReviewPrompts
{
    internal static string BuildPerFileSystemPrompt(
        ReviewSystemContext? context,
        string filePath,
        int fileIndex,
        int totalFiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine(BuildGlobalSystemPrompt(context));
        sb.AppendLine();
        sb.AppendLine(BuildPerFileContextPrompt(context, filePath, fileIndex, totalFiles));
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Builds the per-file context system message that frames the reviewer on which file is currently
    ///     under review. This message is sent on every iteration and retained in message history.
    /// </summary>
    internal static string BuildPerFileContextPrompt(
        ReviewSystemContext? context,
        string filePath,
        int fileIndex,
        int totalFiles)
    {
        if (context?.PromptOverrides.TryGetValue("PerFileContextPrompt", out var overrideText) == true)
        {
            return ComposePrompt(context, PromptStageKeys.PerFileContextSystem, PromptStageRole.System, overrideText!);
        }

        var defaultText = PromptTemplateRuntime.RenderStage(
            PromptStageKeys.PerFileContextSystem,
            new PromptTemplateModels.PerFileContextModel(
                filePath,
                fileIndex,
                totalFiles,
                totalFiles > 1,
                BuildFocusedReviewGuidanceSection(context?.PerFileHint?.FocusedReviewGuidance),
                BuildAgenticPlanSection(context?.PerFileHint),
                context?.PerFileHint?.AgenticInvestigations.Count > 0,
                context?.PerFileHint?.AgenticInvestigations.Select(investigation => new PromptTemplateModels.PromptInvestigationResultModel(
                    investigation.TaskId,
                    investigation.Status,
                    investigation.Evidence.Count > 0,
                    investigation.Evidence.Select(evidence => new PromptTemplateModels.PromptSimpleEvidenceItemModel(
                        evidence.Kind,
                        evidence.Summary,
                        evidence.SourceId ?? "no-source")).ToList(),
                    investigation.CandidateFindings.Count > 0,
                    investigation.CandidateFindings.Select(finding => new PromptTemplateModels.PromptPrWideCandidateFindingModel(
                        finding.Id,
                        finding.Message,
                        finding.Category,
                        $"{finding.Confidence.Concern}={finding.Confidence.Score}",
                        string.Join(", ", finding.RelatedFilePaths))).ToList())).ToList() ?? [],
                OutputKeyReminder));

        return ComposePrompt(context, PromptStageKeys.PerFileContextSystem, PromptStageRole.System, defaultText);
    }

    private static void AppendFocusedReviewGuidanceSection(
        StringBuilder sb,
        IReadOnlyList<FocusedReviewGuidanceItem>? focusedGuidance)
    {
        if (focusedGuidance is not { Count: > 0 })
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("## Focused Review Guidance");
        sb.AppendLine(
            "The following diff-relevant concerns were prefiltered from review knowledge. " +
            "Treat them as targeted investigation cues, not as automatic findings.");

        foreach (var item in focusedGuidance)
        {
            sb.AppendLine();
            sb.AppendLine($"### {item.Id} | {item.Title} (score {item.Score})");

            if (!string.IsNullOrWhiteSpace(item.Reason))
            {
                sb.AppendLine($"Why it may matter: {item.Reason}");
            }

            if (!string.IsNullOrWhiteSpace(item.ShortDescription))
            {
                sb.AppendLine(item.ShortDescription);
            }

            sb.AppendLine(item.Instruction.TrimEnd());
        }
    }

    private static string? BuildFocusedReviewGuidanceSection(IReadOnlyList<FocusedReviewGuidanceItem>? focusedGuidance)
    {
        if (focusedGuidance is not { Count: > 0 })
        {
            return null;
        }

        var sb = new StringBuilder();
        AppendFocusedReviewGuidanceSection(sb, focusedGuidance);
        return sb.ToString().TrimEnd();
    }

    private static string? BuildAgenticPlanSection(PerFileReviewHint? hint)
    {
        if (hint?.AgenticPlan is not { } plan)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Agentic File Plan");
        sb.AppendLine($"Plan ID: {plan.PlanId}");
        sb.AppendLine($"Anchor file: {plan.AnchorFilePath}");

        if (plan.Concerns.Count > 0)
        {
            sb.AppendLine("Concerns:");
            foreach (var concern in plan.Concerns)
            {
                sb.AppendLine($"- {concern}");
            }
        }

        if (plan.InvestigationTasks.Count > 0)
        {
            sb.AppendLine("Investigation tasks:");
            foreach (var task in plan.InvestigationTasks)
            {
                sb.AppendLine(
                    $"- {task.TaskId}: {task.Concern} [{task.TaskType}] (tools: {string.Join(", ", task.AllowedTools)}, budget: {task.MaxToolCalls})");
            }
        }
        else if (!string.IsNullOrWhiteSpace(plan.NoInvestigationReason))
        {
            sb.AppendLine($"No-investigation reason: {plan.NoInvestigationReason}");
        }

        if (hint.AgenticInvestigations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Agentic Investigation Results");
            foreach (var investigation in hint.AgenticInvestigations)
            {
                sb.AppendLine($"### {investigation.TaskId} [{investigation.Status}]");

                if (investigation.Evidence.Count > 0)
                {
                    sb.AppendLine("Evidence:");
                    foreach (var evidence in investigation.Evidence)
                    {
                        sb.AppendLine($"- {evidence.Kind}: {evidence.Summary} ({evidence.SourceId ?? "no-source"})");
                    }
                }

                if (investigation.CandidateFindings.Count > 0)
                {
                    sb.AppendLine("Candidate findings:");
                    foreach (var finding in investigation.CandidateFindings)
                    {
                        sb.AppendLine($"- {finding.Id}: {finding.Message}");
                    }
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    internal static string BuildSynthesisSystemPrompt(ReviewSystemContext? context, bool jsonMode = false)
    {
        if (context?.PromptOverrides.TryGetValue("SynthesisSystemPrompt", out var overrideText) == true)
        {
            return ComposePrompt(context, PromptStageKeys.SynthesisSystem, PromptStageRole.System, overrideText!);
        }

        var defaultText = PromptTemplateRuntime.RenderStage(
            PromptStageKeys.SynthesisSystem,
            new PromptTemplateModels.SynthesisSystemModel(jsonMode));

        return ComposePrompt(context, PromptStageKeys.SynthesisSystem, PromptStageRole.System, defaultText);
    }

    internal static string BuildPerFileUserMessage(
        ChangedFile file,
        int fileIndex,
        int totalFiles,
        IReadOnlyList<ChangedFileSummary> manifest,
        IReadOnlyList<PrCommentThread> relevantThreads,
        string prTitle,
        string sourceBranch,
        string targetBranch,
        ReviewSystemContext? context = null)
    {
        var defaultText = PromptTemplateRuntime.RenderStage(
            PromptStageKeys.PerFileUser,
            new PromptTemplateModels.PerFileUserModel(
                file.Path,
                file.ChangeType.ToString(),
                fileIndex,
                totalFiles,
                prTitle,
                sourceBranch,
                targetBranch,
                manifest.Count > totalFiles,
                manifest.Select(f => new PromptTemplateModels.PromptFileManifestItem(
                    f.Path,
                    f.ChangeType.ToString(),
                    f.Path == file.Path,
                    false)).ToList(),
                file.IsBinary,
                file.UnifiedDiff,
                relevantThreads.Count > 0,
                relevantThreads.Select(thread => new PromptTemplateModels.PromptThreadModel(
                        thread.FilePath is not null
                            ? $"{thread.FilePath}{(thread.LineNumber.HasValue ? $":L{thread.LineNumber}" : string.Empty)}"
                            : "(PR-level)",
                        thread.Comments.Select(comment => new PromptTemplateModels.PromptThreadCommentModel(comment.AuthorName, comment.Content)).ToList()))
                    .ToList()));

        return ComposePrompt(context, PromptStageKeys.PerFileUser, PromptStageRole.User, defaultText);
    }

    internal static string BuildSynthesisUserMessage(
        IReadOnlyList<(string FilePath, string Summary)> perFileSummaries,
        string prTitle,
        string? prDescription,
        IReadOnlyList<ReviewComment>? allComments = null,
        IReadOnlyList<CandidateReviewFinding>? candidateFindings = null,
        ReviewSystemContext? context = null)
    {
        var hasCandidateFindings = candidateFindings is { Count: > 0 };
        var hasComments = allComments is { Count: > 0 };
        var defaultText = PromptTemplateRuntime.RenderStage(
            PromptStageKeys.SynthesisUser,
            new PromptTemplateModels.SynthesisUserModel(
                prTitle,
                prDescription,
                perFileSummaries.Select(summary => new PromptTemplateModels.PromptSummaryItem(summary.FilePath, summary.Summary)).ToList(),
                hasCandidateFindings || hasComments,
                hasCandidateFindings,
                candidateFindings?.Select(finding => new PromptTemplateModels.PromptCandidateFindingItem(
                    finding.FindingId,
                    finding.FilePath ?? "(PR-level)",
                    finding.Severity.ToString().ToLowerInvariant(),
                    finding.Message.Replace("|", "\\|"))).ToList() ?? [],
                allComments?.Select(comment => new PromptTemplateModels.PromptCommentItem(
                    comment.FilePath ?? "(PR-level)",
                    comment.Severity.ToString().ToLowerInvariant(),
                    comment.Message.Replace("|", "\\|"))).ToList() ?? [],
                hasCandidateFindings ? "Use the exact `Finding ID` values from the table for `supportingFindingIds`. " : string.Empty,
                hasCandidateFindings || hasComments));

        return ComposePrompt(context, PromptStageKeys.SynthesisUser, PromptStageRole.User, defaultText);
    }
}
