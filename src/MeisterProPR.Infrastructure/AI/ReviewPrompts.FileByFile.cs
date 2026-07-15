// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
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
        // A security-lens pass renders the dedicated security-specialist template. The per-client PerFileContextPrompt
        // override applies only to the ordinary per-file context prompt, not to a specialist lens pass.
        var isSecurityLens = string.Equals(context?.ActiveLens, ReviewPassLens.Security, StringComparison.Ordinal);
        var stageKey = isSecurityLens
            ? PromptStageKeys.PerFileSecurityLensContextSystem
            : PromptStageKeys.PerFileContextSystem;

        if (!isSecurityLens && context?.PromptOverrides.TryGetValue("PerFileContextPrompt", out var overrideText) == true)
        {
            return ComposePrompt(context, PromptStageKeys.PerFileContextSystem, PromptStageRole.System, overrideText!);
        }

        // The design-review scope widens the per-file reviewer mandate to substantive design/quality
        // concerns. It rides on aggressiveness (on for Balanced/Assertive, off for Calm and null context),
        // mirroring the certainty-gate flag in the global system prompt.
        var designReviewScope = context?.Aggressiveness is ReviewAggressiveness.Balanced or ReviewAggressiveness.Assertive;

        var defaultText = PromptTemplateRuntime.RenderStage(
            stageKey,
            new PromptTemplateModels.PerFileContextModel(
                filePath,
                fileIndex,
                totalFiles,
                totalFiles > 1,
                BuildFocusedReviewGuidanceSection(context?.PerFileHint?.FocusedReviewGuidance),
                BuildSecurityChecklistSection(context?.PerFileHint?.RiskMarkers, context?.PerFileHint?.FilePath),
                BuildAgenticPlanSection(context?.PerFileHint),
                context?.PerFileHint?.PrefetchedContextEvidence.Count > 0,
                context?.PerFileHint?.PrefetchedContextEvidence.Select(item => new PromptTemplateModels.PromptPrefetchedContextEvidenceModel(
                    item.Kind,
                    item.Title,
                    item.SourceId,
                    item.Content,
                    item.Truncated)).ToList() ?? [],
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
                OutputKeyReminder,
                designReviewScope));

        return ComposePrompt(context, stageKey, PromptStageRole.System, defaultText);
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

        AppendConcerns(sb, plan.Concerns);
        AppendInvestigationTasksOrReason(sb, plan);
        AppendInvestigationResults(sb, hint.AgenticInvestigations);

        return sb.ToString().TrimEnd();
    }

    private static void AppendConcerns(StringBuilder sb, IReadOnlyList<string> concerns)
    {
        if (concerns.Count == 0)
        {
            return;
        }

        sb.AppendLine("Concerns:");
        foreach (var concern in concerns)
        {
            sb.AppendLine($"- {concern}");
        }
    }

    private static void AppendInvestigationTasksOrReason(StringBuilder sb, AgenticFileReviewPlan plan)
    {
        if (plan.InvestigationTasks.Count > 0)
        {
            sb.AppendLine("Investigation tasks:");
            foreach (var task in plan.InvestigationTasks)
            {
                sb.AppendLine(
                    $"- {task.TaskId}: {task.Concern} [{task.TaskType}] (tools: {string.Join(", ", task.AllowedTools)}, budget: {task.MaxToolCalls})");
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(plan.NoInvestigationReason))
        {
            sb.AppendLine($"No-investigation reason: {plan.NoInvestigationReason}");
        }
    }

    private static void AppendInvestigationResults(StringBuilder sb, IReadOnlyList<AgenticFileInvestigationResult> investigations)
    {
        if (investigations.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("## Agentic Investigation Results");
        foreach (var investigation in investigations)
        {
            sb.AppendLine($"### {investigation.TaskId} [{investigation.Status}]");
            AppendEvidence(sb, investigation.Evidence);
            AppendCandidateFindings(sb, investigation.CandidateFindings);
        }
    }

    private static void AppendEvidence(StringBuilder sb, IReadOnlyList<EvidenceItem> evidence)
    {
        if (evidence.Count == 0)
        {
            return;
        }

        sb.AppendLine("Evidence:");
        foreach (var item in evidence)
        {
            sb.AppendLine($"- {item.Kind}: {item.Summary} ({item.SourceId ?? "no-source"})");
        }
    }

    private static void AppendCandidateFindings(StringBuilder sb, IReadOnlyList<AgenticFileCandidateFinding> findings)
    {
        if (findings.Count == 0)
        {
            return;
        }

        sb.AppendLine("Candidate findings:");
        foreach (var finding in findings)
        {
            sb.AppendLine($"- {finding.Id}: {finding.Message}");
        }
    }

    private static string? BuildSecurityChecklistSection(FileRiskMarkers? riskMarkers, string? filePath)
    {
        var markers = riskMarkers ?? FileRiskMarkers.None;
        if (!SecurityFloor.IsFlagged(filePath, markers, false))
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Security Specialist Checklist");
        sb.AppendLine("This file is security-relevant. Perform an explicit specialist check in addition to the general review.");
        if (markers.MatchedMarkers.Count > 0)
        {
            sb.AppendLine("Matched markers: " + string.Join(", ", markers.MatchedMarkers));
        }

        if (SecurityFloor.IsSecuritySensitivePath(filePath))
        {
            sb.AppendLine("This file is in a security-sensitive path.");
        }

        sb.AppendLine(
            "- Check authentication, authorization, token/secret handling, redirect, iframe/frame, origin/referer, input validation, and allow/deny-list logic for concrete exploitable breakage.");
        sb.AppendLine("Report only concrete correctness or security failures grounded in the supplied diff and gathered evidence.");
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
                BuildBoundedManifest(file.Path, manifest).Select(f => new PromptTemplateModels.PromptFileManifestItem(
                    f.Path,
                    f.ChangeType.ToString(),
                    f.Path == file.Path,
                    false)).ToList(),
                file.IsBinary,
                ReviewDiffProcessor.AnnotateUnifiedDiffWithNewLineNumbers(file.UnifiedDiff),
                relevantThreads.Count > 0,
                relevantThreads.Select(thread => new PromptTemplateModels.PromptThreadModel(
                        FormatThreadLocation(thread),
                        thread.Comments.Select(comment => new PromptTemplateModels.PromptThreadCommentModel(comment.AuthorName, comment.Content)).ToList()))
                    .ToList()));

        return ComposePrompt(context, PromptStageKeys.PerFileUser, PromptStageRole.User, defaultText);
    }

    private static string FormatThreadLocation(PrCommentThread thread)
    {
        if (thread.FilePath is null)
        {
            return "(PR-level)";
        }

        return thread.LineNumber.HasValue
            ? $"{thread.FilePath}:L{thread.LineNumber}"
            : thread.FilePath;
    }

    private static IReadOnlyList<ChangedFileSummary> BuildBoundedManifest(string currentPath, IReadOnlyList<ChangedFileSummary> manifest)
    {
        var current = manifest.FirstOrDefault(file => string.Equals(file.Path, currentPath, StringComparison.Ordinal));
        if (manifest.Count <= 8)
        {
            return manifest;
        }

        var bounded = new List<ChangedFileSummary>();
        if (current is not null)
        {
            bounded.Add(current);
        }

        bounded.AddRange(manifest.Where(file => !string.Equals(file.Path, currentPath, StringComparison.Ordinal)).Take(7));
        return bounded;
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
