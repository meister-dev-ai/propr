// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.AI;

internal static class AgenticFileArtifactParser
{
    internal static bool TryParsePlan(string? responseText, out AgenticFileReviewPlan? plan)
    {
        plan = null;
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(StripMarkdownCodeFences(responseText));
            var root = doc.RootElement;
            var planId = GetString(root, "plan_id") ?? "plan-001";
            var anchorFilePath = GetString(root, "anchor_file_path");
            if (string.IsNullOrWhiteSpace(anchorFilePath))
            {
                return false;
            }

            var tasks = new List<AgenticFileInvestigationTask>();
            if (root.TryGetProperty("investigation_tasks", out var tasksEl) && tasksEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tasksEl.EnumerateArray())
                {
                    var taskId = GetString(item, "id");
                    var concern = GetString(item, "concern");
                    if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(concern))
                    {
                        continue;
                    }

                    tasks.Add(
                        new AgenticFileInvestigationTask(
                            taskId,
                            GetString(item, "task_type") ?? "concern",
                            GetString(item, "trigger_family") ?? "explicit_follow_up_signal",
                            concern,
                            GetStringArray(item, "seed_file_paths"),
                            GetStringArray(item, "allowed_tools"),
                            GetInt(item, "max_tool_calls") ?? 1));
                }
            }

            plan = new AgenticFileReviewPlan(
                planId,
                anchorFilePath,
                GetStringArray(root, "concerns"),
                GetStringArray(root, "changed_areas"),
                tasks,
                GetString(root, "no_investigation_reason"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseInvestigationResult(
        string? responseText,
        AgenticFileInvestigationTask task,
        out AgenticFileInvestigationResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(StripMarkdownCodeFences(responseText));
            var root = doc.RootElement;

            var evidence = ParseEvidence(root);
            var findings = ParseCandidateFindings(root, task);
            var toolUsage = ParseToolUsage(root);

            result = new AgenticFileInvestigationResult(
                GetString(root, "task_id") ?? task.TaskId,
                GetString(root, "status") ?? "completed",
                evidence,
                findings,
                toolUsage,
                GetBool(root, "degraded") ?? false,
                GetBool(root, "diagnostics_only") ?? false,
                GetString(root, "evidence_set_id"),
                GetBool(root, "dependency_recorded") ?? false);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<EvidenceItem> ParseEvidence(JsonElement root)
    {
        var evidence = new List<EvidenceItem>();
        if (!root.TryGetProperty("evidence", out var evidenceEl) || evidenceEl.ValueKind != JsonValueKind.Array)
        {
            return evidence;
        }

        foreach (var item in evidenceEl.EnumerateArray())
        {
            var kind = GetString(item, "kind");
            var summary = GetString(item, "summary");
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(summary))
            {
                continue;
            }

            evidence.Add(new EvidenceItem(kind, summary, GetString(item, "source_id")));
        }

        return evidence;
    }

    private static List<AgenticFileCandidateFinding> ParseCandidateFindings(JsonElement root, AgenticFileInvestigationTask task)
    {
        var findings = new List<AgenticFileCandidateFinding>();
        if (!root.TryGetProperty("candidate_findings", out var findingsEl) || findingsEl.ValueKind != JsonValueKind.Array)
        {
            return findings;
        }

        foreach (var item in findingsEl.EnumerateArray())
        {
            var id = GetString(item, "id");
            var message = GetString(item, "message");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            var supportingFiles = GetStringArray(item, "supporting_files");
            findings.Add(
                new AgenticFileCandidateFinding(
                    id,
                    message,
                    GetString(item, "category") ?? CandidateReviewFinding.PerFileCommentCategory,
                    ParseConfidence(item.TryGetProperty("confidence", out var confidenceEl) ? confidenceEl : default),
                    new EvidenceReference(
                        [],
                        supportingFiles,
                        supportingFiles.Count >= 2 ? EvidenceReference.ResolvedState : EvidenceReference.PartialState,
                        "agentic_file_investigation"),
                    supportingFiles.Count > 0 ? supportingFiles : task.SeedFilePaths,
                    ParseSeverity(item),
                    GetString(item, "file_path"),
                    GetInt(item, "line_number"),
                    GetString(item, "candidate_summary_text"),
                    SupportSource: GetString(item, "support_source")));
        }

        return findings;
    }

    private static List<AgenticFileToolUsage> ParseToolUsage(JsonElement root)
    {
        var toolUsage = new List<AgenticFileToolUsage>();
        if (!root.TryGetProperty("tool_usage", out var toolUsageEl) || toolUsageEl.ValueKind != JsonValueKind.Array)
        {
            return toolUsage;
        }

        foreach (var item in toolUsageEl.EnumerateArray())
        {
            var toolName = GetString(item, "tool_name");
            var status = GetString(item, "status");
            if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(status))
            {
                continue;
            }

            toolUsage.Add(new AgenticFileToolUsage(toolName, status, GetString(item, "target")));
        }

        return toolUsage;
    }

    private static ConfidenceScore ParseConfidence(JsonElement element)
    {
        var concern = element.ValueKind == JsonValueKind.Object
            ? GetString(element, "concern") ?? "correctness"
            : "correctness";
        var score = element.ValueKind == JsonValueKind.Object
            ? GetInt(element, "score") ?? 70
            : 70;
        return new ConfidenceScore(concern, score);
    }

    private static CommentSeverity ParseSeverity(JsonElement element)
    {
        return GetString(element, "severity")?.Trim().ToLowerInvariant() switch
        {
            "error" => CommentSeverity.Error,
            "suggestion" => CommentSeverity.Suggestion,
            "info" => CommentSeverity.Info,
            _ => CommentSeverity.Warning,
        };
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static int? GetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool? GetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static string StripMarkdownCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed.Trim('`');
        }

        var withoutOpenFence = trimmed[(firstNewline + 1)..];
        var closingFenceIndex = withoutOpenFence.LastIndexOf("```", StringComparison.Ordinal);
        return closingFenceIndex >= 0
            ? withoutOpenFence[..closingFenceIndex].Trim()
            : withoutOpenFence.Trim();
    }
}
