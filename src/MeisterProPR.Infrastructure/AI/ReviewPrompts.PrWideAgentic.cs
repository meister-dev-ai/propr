// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.AI;

internal static partial class ReviewPrompts
{
    internal static string BuildPrWidePlanningSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("PrWidePlanningSystemPrompt", out var overrideText) == true)
        {
            return overrideText!;
        }

        return """
               You are running Stage A of a PR-wide agentic review workflow.
               Your job is to understand the pull request globally, prioritize the highest-risk changed areas,
               and produce only bounded investigation tasks for later stages.

               Rules:
               1. Prefer a small number of high-value investigations over exhaustive coverage.
               2. Focus on configuration, entrypoint, dependency registration, contracts, migrations, tests, and cross-file behavior when present.
               3. Do not write final review comments, publishable findings, or speculative user-facing advice.
               4. Every investigation task must remain bounded to explicit files and an explicit tool budget.

               Respond with a single raw JSON object only.
               Schema:
               {
                 "plan_id": "<stable id>",
                 "concerns": ["<concern>"],
                 "changed_areas": ["<path or area>"],
                 "investigation_tasks": [
                   {
                     "id": "task-001",
                     "task_type": "concern"|"file"|"file_group"|"architectural_slice",
                     "concern": "<bounded hypothesis>",
                     "seed_file_paths": ["<relative path>"],
                     "allowed_tools": ["get_file_content","get_file_tree","get_changed_files","ask_procursor_knowledge","get_procursor_symbol_info"],
                     "max_tool_calls": <positive integer>
                   }
                 ],
                 "no_investigation_reason": "<nullable explanation>"
               }
               """;
    }

    internal static string BuildPrWidePlanningUserMessage(PullRequest pr)
    {
        ArgumentNullException.ThrowIfNull(pr);

        var sb = new StringBuilder();
        sb.AppendLine($"PR: {pr.Title}");
        sb.AppendLine($"Source branch: {pr.SourceBranch}");
        sb.AppendLine($"Target branch: {pr.TargetBranch}");
        sb.AppendLine($"Changed file count: {pr.ChangedFiles.Count}");

        if (!string.IsNullOrWhiteSpace(pr.Description))
        {
            sb.AppendLine($"Description: {pr.Description}");
        }

        sb.AppendLine();
        sb.AppendLine("Changed file manifest:");
        foreach (var file in pr.ChangedFiles)
        {
            sb.AppendLine($"- {file.Path} [{file.ChangeType}]");
        }

        sb.AppendLine();
        sb.AppendLine("Diff excerpts:");
        foreach (var file in pr.ChangedFiles)
        {
            sb.AppendLine();
            sb.AppendLine($"## {file.Path}");
            sb.AppendLine(file.IsBinary ? "[binary file omitted]" : file.UnifiedDiff);
        }

        return sb.ToString().TrimEnd();
    }

    internal static string BuildPrWideInvestigationSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("PrWideInvestigationSystemPrompt", out var overrideText) == true)
        {
            return overrideText!;
        }

        return """
               You are running Stage B of a PR-wide agentic review workflow.
               Your job is to test one bounded concern using only the provided scope, tool budget, and allowed tools.
               Do not produce final review comments. Do not expand scope beyond the assigned seed files.

               Respond with a single raw JSON object only.
               Schema:
               {
                 "status": "completed"|"skipped"|"degraded",
                 "evidence": [
                   { "kind": "file_content", "summary": "<summary>", "source_id": "<nullable source>" }
                 ],
                 "candidate_findings": [
                   {
                     "id": "candidate-001",
                     "message": "<candidate message>",
                     "category": "cross_cutting",
                     "confidence": { "concern": "<area>", "score": <0-100> },
                     "supporting_files": ["<relative path>"]
                   }
                 ],
                 "degraded": true|false
               }
               """;
    }

    internal static string BuildPrWideInvestigationUserMessage(
        PrWideReviewPlan plan,
        PrWideInvestigationTask task,
        PullRequest pr)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(pr);

        var sb = new StringBuilder();
        sb.AppendLine($"Plan ID: {plan.PlanId}");
        sb.AppendLine($"Task ID: {task.Id}");
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

        return sb.ToString().TrimEnd();
    }

    internal static string BuildPrWideSynthesisSystemPrompt(ReviewSystemContext? context)
    {
        if (context?.PromptOverrides.TryGetValue("PrWideSynthesisSystemPrompt", out var overrideText) == true)
        {
            return overrideText!;
        }

        return """
               You are running Stage C of a PR-wide agentic review workflow.
               Merge overlapping investigation outputs into ranked candidate findings.
               Do not make final publication decisions.

               Respond with a single raw JSON object only.
               Schema:
               {
                 "candidate_findings": [
                   {
                     "id": "candidate-001",
                     "message": "<candidate message>",
                     "category": "cross_cutting|per_file_comment|architecture|documentation|test|ui|configuration|robustness|non_actionable",
                     "severity": "info|warning|error|suggestion",
                     "confidence": { "concern": "<area>", "score": <0-100> },
                     "supporting_files": ["<relative path>"],
                     "supporting_finding_ids": ["<candidate or investigation finding id>"],
                     "candidate_summary_text": "<summary-only wording when publication is not justified>",
                     "file_path": "<relative path or null>",
                     "line_number": <positive integer or null>,
                     "evidence_resolution_state": "resolved|partial|missing",
                     "evidence_source": "pr_wide_synthesis"
                   }
                 ],
                 "summary": "<draft PR-wide summary before final reconciliation>"
               }

               Rules:
               1. Preserve actionable local findings with `file_path` and `line_number` when they map to one changed line.
               2. Use `file_path: null` and `line_number: null` for valid PR-level findings that should never be forced onto a misleading anchor.
               3. Use `candidate_summary_text` for any finding that may need `SummaryOnly` disposition later.
               4. Do not make final publication decisions. Emit only candidate findings plus the draft PR summary.
               """;
    }

    internal static string BuildPrWideSynthesisUserMessage(
        PrWideReviewPlan plan,
        IReadOnlyList<PrWideInvestigationResult> investigations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(investigations);

        var sb = new StringBuilder();
        sb.AppendLine($"Plan ID: {plan.PlanId}");
        sb.AppendLine("Concerns:");
        foreach (var concern in plan.Concerns)
        {
            sb.AppendLine($"- {concern}");
        }

        sb.AppendLine();
        sb.AppendLine("Investigation outputs:");
        foreach (var result in investigations)
        {
            sb.AppendLine();
            sb.AppendLine($"## {result.TaskId} [{result.Status}]");

            if (result.Evidence.Count > 0)
            {
                sb.AppendLine("Evidence:");
                foreach (var evidence in result.Evidence)
                {
                    sb.AppendLine($"- {evidence.Kind}: {evidence.Summary} ({evidence.SourceId ?? "no-source"})");
                }
            }

            if (result.CandidateFindings.Count > 0)
            {
                sb.AppendLine("Candidate findings:");
                foreach (var finding in result.CandidateFindings)
                {
                    sb.AppendLine($"- {finding.Id}: {finding.Message}");
                    sb.AppendLine($"  Category: {finding.Category}");
                    sb.AppendLine($"  Confidence: {finding.Confidence.Concern}={finding.Confidence.Score}");
                    sb.AppendLine($"  Supporting files: {string.Join(", ", finding.RelatedFilePaths)}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }
}
