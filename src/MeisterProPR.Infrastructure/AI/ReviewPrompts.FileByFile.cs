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

        var sb = new StringBuilder();
        sb.Append(
            $"""
             When a change introduces or extends a user-visible feature path, check whether the changed code itself shows that comparable functionality becomes reachable through an established shared mechanism such as registration, navigation, routing, or execution wiring.
             If the diff does not introduce or modify a route, registration point, navigation item, execution entrypoint, or other concrete reachability mechanism, treat this shared-mechanism guidance as not applicable and review the bounded code change normally.
             Only prefer a broken integration or discoverability critique as the root cause when the supplied code directly demonstrates both: (1) the new path is created or rendered, and (2) the expected shared mechanism is the concrete way similar paths become reachable, and this change omitted that wiring.
             Do not broaden a concrete bounded defect into a higher-level architecture or registration complaint just because the code looks similar to existing patterns. If the broader claim depends on assumptions about other files, missing conventions, or intended product structure, keep the finding bounded.
             If a single-file defect fully explains the user-visible failure, prefer reporting that bounded defect first. Escalate to the broader shared-mechanism critique only when the changed code makes the missing wiring explicit and materially more explanatory than the bounded defect.
             You are reviewing **{filePath}** ({fileIndex} of {totalFiles}). The other changed files are listed in the manifest below — their content is not provided. Call `get_file_content` on any sibling file when its content is needed for an accurate analysis of the file under review.
             """);

        if (totalFiles > 1)
        {
            sb.AppendLine();
            sb.AppendLine(
                "**Mandatory investigation requirement**: Before emitting your final review JSON, you MUST call " +
                "`get_file_content` on every manifest file you reference conditionally in a pending finding. " +
                "Set `investigation_complete` to `true` only after you have done so. " +
                "If there are no related files worth inspecting, set `investigation_complete` to `true` and explain why in the summary.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(
                "This is the only file changed in the PR. " +
                "No sibling-file investigation is required; you may set `investigation_complete` to `true` immediately.");
        }

        AppendFocusedReviewGuidanceSection(sb, context?.PerFileHint?.FocusedReviewGuidance);

        if (context?.PerFileHint?.AgenticPlan is { } plan)
        {
            sb.AppendLine();
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

            if (context.PerFileHint.AgenticInvestigations.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Agentic Investigation Results");
                foreach (var investigation in context.PerFileHint.AgenticInvestigations)
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
        }

        sb.AppendLine();
        sb.AppendLine(OutputKeyReminder);

        return ComposePrompt(context, PromptStageKeys.PerFileContextSystem, PromptStageRole.System, sb.ToString().TrimEnd());
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

    internal static string BuildSynthesisSystemPrompt(ReviewSystemContext? context, bool jsonMode = false)
    {
        if (context?.PromptOverrides.TryGetValue("SynthesisSystemPrompt", out var overrideText) == true)
        {
            return ComposePrompt(context, PromptStageKeys.SynthesisSystem, PromptStageRole.System, overrideText!);
        }

        if (jsonMode)
        {
            return ComposePrompt(
                context,
                PromptStageKeys.SynthesisSystem,
                PromptStageRole.System,
                """
                You are an expert code reviewer. You will be given a set of per-file review summaries and findings for a pull request.

                When consolidating per-file results, preserve a file-level finding as a bounded defect when the changed code directly demonstrates the problem and its user-visible consequence.

                Only emit a cross-cutting concern when the evidence spans multiple files, multiple independently supported findings, or a broader pattern that cannot be expressed accurately as an existing bounded finding.

                Do not restate a single supported file-level finding as a broader architectural concern unless the broader concern is separately supported by the supplied evidence.

                Your task: write a single cohesive narrative summary for the overall pull request and identify any cross-cutting concerns.
                Focus on the most important findings across all files.
                Do not invent new findings that are not mentioned in the per-file summaries.
                Do not call any tools.
                Respond with a single raw JSON object ONLY — no markdown fences, no prose before or after.
                The very first character must be '{' and the very last character must be '}'.
                Schema: { "summary": "<overall narrative>", "cross_cutting_concerns": [{ "message": "<concern>", "severity": "<info|warning|error|suggestion>", "category": "<cross_cutting|architecture|documentation|test|ui|configuration|robustness|non_actionable>", "candidateSummaryText": "<summary-only wording>", "supportingFindingIds": ["<finding-id>"], "supportingFiles": ["<path>"], "evidenceResolutionState": "<resolved|missing|partial>", "evidenceSource": "<synthesis_payload>" }] }
                """);
        }

        return ComposePrompt(
            context,
            PromptStageKeys.SynthesisSystem,
            PromptStageRole.System,
            """
            You are an expert code reviewer. You will be given a set of per-file review summaries for a pull request.

            When consolidating per-file results, preserve a file-level finding as a bounded defect when the changed code directly demonstrates the problem and its user-visible consequence.

            Only emit a cross-cutting concern when the evidence spans multiple files, multiple independently supported findings, or a broader pattern that cannot be expressed accurately as an existing bounded finding.

            Do not restate a single supported file-level finding as a broader architectural concern unless the broader concern is separately supported by the supplied evidence.

            Your task: write a single cohesive narrative summary for the overall pull request.
            Focus on the most important findings across all files.
            Do not invent new findings that are not mentioned in the per-file summaries.
            Do not call any tools.
            Respond with plain text only — no JSON, no markdown fences, no bullet lists unless they aid clarity.
            """);
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
        var sb = new StringBuilder();

        // Header: identify file under review
        sb.AppendLine($"Reviewing file {fileIndex} of {totalFiles}: {file.Path} [{file.ChangeType}]");
        sb.AppendLine($"PR: {prTitle} ({sourceBranch} → {targetBranch})");
        sb.AppendLine($"Source branch (use this with get_file_content): {sourceBranch}");
        sb.AppendLine();

        // Change manifest — paths + change types only, no content
        sb.AppendLine("Changed files in this PR (use get_file_content to inspect any):");
        if (manifest.Count > totalFiles)
        {
            sb.AppendLine(
                $"  Note: this is a re-review pass. Only {totalFiles} newly changed file(s) are under " +
                "active review; the remaining files listed below are provided as context only.");
        }

        foreach (var f in manifest)
        {
            var marker = f.Path == file.Path ? " [CURRENT FILE]" : string.Empty;
            sb.AppendLine($"- {f.Path} [{f.ChangeType}]{marker}");
        }

        sb.AppendLine();

        // File under review — diff only
        sb.AppendLine("======================================= FILE UNDER REVIEW =======================================");
        if (file.IsBinary)
        {
            sb.AppendLine($"=== {file.Path} [{file.ChangeType}] === [binary file — content omitted]");
        }
        else
        {
            sb.AppendLine($"=== {file.Path} [{file.ChangeType}] ===");
            sb.AppendLine("======================================= DIFF =======================================");
            sb.AppendLine(file.UnifiedDiff);
            sb.AppendLine("======================================= END DIFF =======================================");
            sb.AppendLine("(Full file content is not included. Call `get_file_content` on this file if the diff is insufficient for a complete analysis.)");
        }

        // Filtered threads (only threads for this file + PR-level threads)
        if (relevantThreads.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Existing Review Threads");
            sb.AppendLine(
                "The following threads already exist on this PR. Take them into account: " +
                "avoid re-flagging resolved issues, and consider developer responses.");

            foreach (var thread in relevantThreads)
            {
                var location = thread.FilePath is not null
                    ? $"{thread.FilePath}{(thread.LineNumber.HasValue ? $":L{thread.LineNumber}" : string.Empty)}"
                    : "(PR-level)";

                sb.AppendLine();
                sb.AppendLine($"### Thread at {location}");
                foreach (var comment in thread.Comments)
                {
                    sb.AppendLine($"  [{comment.AuthorName}]: {comment.Content}");
                }
            }
        }

        return ComposePrompt(context, PromptStageKeys.PerFileUser, PromptStageRole.User, sb.ToString());
    }

    internal static string BuildSynthesisUserMessage(
        IReadOnlyList<(string FilePath, string Summary)> perFileSummaries,
        string prTitle,
        string? prDescription,
        IReadOnlyList<ReviewComment>? allComments = null,
        IReadOnlyList<CandidateReviewFinding>? candidateFindings = null,
        ReviewSystemContext? context = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"The following per-file review summaries were produced for PR \"{prTitle}\":");
        if (!string.IsNullOrWhiteSpace(prDescription))
        {
            sb.AppendLine($"PR Description: {prDescription}");
        }

        sb.AppendLine();

        foreach (var (filePath, summary) in perFileSummaries)
        {
            sb.AppendLine($"## {filePath}");
            sb.AppendLine(summary);
            sb.AppendLine();
        }

        if (candidateFindings is { Count: > 0 } || allComments is { Count: > 0 })
        {
            sb.AppendLine("## All Per-File Findings");
            sb.AppendLine();

            if (candidateFindings is { Count: > 0 })
            {
                sb.AppendLine("| Finding ID | File | Severity | Message |");
                sb.AppendLine("|------------|------|----------|---------|");
                foreach (var finding in candidateFindings)
                {
                    var file = finding.FilePath ?? "(PR-level)";
                    var severity = finding.Severity.ToString().ToLowerInvariant();
                    var msg = finding.Message.Replace("|", "\\|");
                    sb.AppendLine($"| {finding.FindingId} | {file} | {severity} | {msg} |");
                }
            }
            else if (allComments is { Count: > 0 })
            {
                sb.AppendLine("| File | Severity | Message |");
                sb.AppendLine("|------|----------|---------|");
                foreach (var c in allComments)
                {
                    var file = c.FilePath ?? "(PR-level)";
                    var severity = c.Severity.ToString().ToLowerInvariant();
                    var msg = c.Message.Replace("|", "\\|");
                    sb.AppendLine($"| {file} | {severity} | {msg} |");
                }
            }

            sb.AppendLine();
            sb.AppendLine(
                "Based on the above findings, identify any cross-cutting concerns — issues that span multiple files or form a coherent architectural or behavioral pattern. " +
                "Return a JSON object with a `cross_cutting_concerns` array field. Each entry should be: " +
                "{ \"message\": \"<concern description>\", \"severity\": \"<info|warning|error|suggestion>\", \"category\": \"<cross_cutting|architecture|documentation|test|ui|configuration|robustness|non_actionable>\", \"candidateSummaryText\": \"<summary-only wording>\", \"supportingFindingIds\": [\"<finding-id>\"], \"supportingFiles\": [\"<path>\"], \"evidenceResolutionState\": \"<resolved|missing|partial>\", \"evidenceSource\": \"<synthesis_payload>\" }. " +
                (candidateFindings is { Count: > 0 }
                    ? "Use the exact `Finding ID` values from the table for `supportingFindingIds`. "
                    : string.Empty) +
                "If there are no cross-cutting concerns, return { \"cross_cutting_concerns\": [] }. " +
                "Also write a single cohesive narrative summary for the PR. Wrap the summary in a `summary` field in the same JSON object.");
        }
        else
        {
            sb.AppendLine(
                "Write a single cohesive narrative summary for the overall pull request. Focus on the most important findings across all files. Do not invent new findings. Respond with plain text only.");
        }

        return ComposePrompt(context, PromptStageKeys.SynthesisUser, PromptStageRole.User, sb.ToString());
    }
}
