// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
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

        var sb = new StringBuilder();
        sb.AppendLine(
            """
            You are running Stage A of an agentic file-scoped review workflow.
            Your job is to assess one anchor file, decide which concerns warrant bounded follow-up,
            and produce only file-scoped planning artifacts for later steps.

            Rules:
            1. Keep the plan file-scoped. The anchor file remains the publication target.
            2. Only start Stage B when an explicit trigger family justifies deeper follow-up.
            3. Straightforward files with no explicit trigger must produce zero investigation tasks.
            4. Only include sibling files when they are needed to validate a concern in the anchor file.
            5. Prefer a small number of high-value investigations over exhaustive coverage.
            6. Do not produce final review comments or publishable findings.

            Allowed trigger families:
            - configuration_or_wiring
            - dispatch_or_registration
            - bounded_cross_file_consistency
            - explicit_local_evidence_gap
            - symbol_or_api_context
            """);

        AppendFocusedReviewGuidanceSection(sb, context?.PerFileHint?.FocusedReviewGuidance);

        sb.AppendLine();
        sb.AppendLine(
            """
            Respond with a single raw JSON object only.
            Schema:
            {
              "plan_id": "<stable id>",
              "anchor_file_path": "<relative path>",
              "concerns": ["<concern>"],
              "changed_areas": ["<path or area>"],
              "investigation_tasks": [
                 {
                   "id": "task-001",
                   "task_type": "concern"|"sibling_file"|"context_lookup",
                   "trigger_family": "configuration_or_wiring"|"dispatch_or_registration"|"bounded_cross_file_consistency"|"explicit_local_evidence_gap"|"symbol_or_api_context",
                   "concern": "<bounded hypothesis>",
                   "seed_file_paths": ["<relative path>"],
                   "allowed_tools": ["get_file_content","get_file_tree","get_changed_files"],
                   "max_tool_calls": <positive integer>
                }
              ],
              "no_investigation_reason": "<nullable explanation>"
            }
            """);

        return ComposePrompt(context, PromptStageKeys.AgenticFilePlanningSystem, PromptStageRole.System, sb.ToString().TrimEnd());
    }

    internal static string BuildAgenticFilePlanningUserMessage(ChangedFile file, PullRequest pr, ReviewSystemContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(pr);

        var sb = new StringBuilder();
        sb.AppendLine($"PR: {pr.Title}");
        sb.AppendLine($"Source branch: {pr.SourceBranch}");
        sb.AppendLine($"Target branch: {pr.TargetBranch}");
        sb.AppendLine($"Anchor file: {file.Path}");

        if (!string.IsNullOrWhiteSpace(pr.Description))
        {
            sb.AppendLine($"Description: {pr.Description}");
        }

        sb.AppendLine();
        sb.AppendLine("Changed file manifest:");
        foreach (var changedFile in pr.ChangedFiles)
        {
            var marker = changedFile.Path == file.Path ? " [ANCHOR FILE]" : string.Empty;
            sb.AppendLine($"- {changedFile.Path} [{changedFile.ChangeType}]{marker}");
        }

        sb.AppendLine();
        sb.AppendLine($"Anchor file diff for {file.Path}:");
        sb.AppendLine(file.IsBinary ? "[binary file omitted]" : file.UnifiedDiff);

        return ComposePrompt(context, PromptStageKeys.AgenticFilePlanningUser, PromptStageRole.User, sb.ToString().TrimEnd());
    }

    internal static string BuildAgenticFileInvestigationSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("AgenticFileInvestigationSystemPrompt", out var overrideText) == true)
        {
            return ComposePrompt(context, PromptStageKeys.AgenticFileInvestigationSystem, PromptStageRole.System, overrideText!);
        }

        return ComposePrompt(
            context,
            PromptStageKeys.AgenticFileInvestigationSystem,
            PromptStageRole.System,
            """
            You are running Stage B of an agentic file-scoped review workflow.
            Your job is to test one bounded concern for the anchor file using only the provided scope,
            tool budget, and allowed tools. Do not produce final review comments.

            Respond with a single raw JSON object only.
            Schema:
            {
              "task_id": "task-001",
              "status": "completed"|"skipped"|"degraded",
              "evidence": [
                { "kind": "file_content", "summary": "<summary>", "source_id": "<nullable source>" }
              ],
              "candidate_findings": [
                {
                  "id": "candidate-001",
                  "message": "<candidate message>",
                  "category": "per_file_comment|cross_cutting|configuration|robustness|test|documentation|non_actionable",
                  "severity": "info|warning|error|suggestion",
                  "confidence": { "concern": "<area>", "score": <0-100> },
                  "supporting_files": ["<relative path>"],
                  "file_path": "<relative path or null>",
                  "line_number": <positive integer or null>,
                  "candidate_summary_text": "<nullable summary wording>"
                }
              ],
              "tool_usage": [
                { "tool_name": "get_file_content", "status": "success|blocked_not_allowed|blocked_budget_exhausted|blocked_scope_violation|failed", "target": "<nullable target>" }
               ],
               "degraded": true|false
             }
            """);
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

        var sb = new StringBuilder();
        sb.AppendLine($"Plan ID: {plan.PlanId}");
        sb.AppendLine($"Anchor file: {plan.AnchorFilePath}");
        sb.AppendLine($"Task ID: {task.TaskId}");
        sb.AppendLine($"Task type: {task.TaskType}");
        sb.AppendLine($"Concern: {task.Concern}");
        sb.AppendLine($"Source branch: {pr.SourceBranch}");
        sb.AppendLine($"Allowed tools: {string.Join(", ", task.AllowedTools)}");
        sb.AppendLine($"Max tool calls: {task.MaxToolCalls}");
        sb.AppendLine();
        sb.AppendLine("Seed files:");
        foreach (var path in task.SeedFilePaths)
        {
            sb.AppendLine($"- {path}");
        }

        return ComposePrompt(context, PromptStageKeys.AgenticFileInvestigationUser, PromptStageRole.User, sb.ToString().TrimEnd());
    }
}
