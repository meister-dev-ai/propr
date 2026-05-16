// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;

internal static class PrWideArtifactParser
{
    internal static bool TryParsePlan(
        string? responseText,
        out PrWideReviewPlan? plan)
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
            var concerns = GetStringArray(root, "concerns");
            var changedAreas = GetStringArray(root, "changed_areas");
            var noInvestigationReason = GetString(root, "no_investigation_reason");
            var tasks = new List<PrWideInvestigationTask>();

            if (root.TryGetProperty("investigation_tasks", out var tasksEl) && tasksEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in tasksEl.EnumerateArray())
                {
                    var id = GetString(item, "id");
                    var concern = GetString(item, "concern");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(concern))
                    {
                        continue;
                    }

                    tasks.Add(
                        new PrWideInvestigationTask(
                            id,
                            GetString(item, "task_type") ?? "concern",
                            concern,
                            GetStringArray(item, "seed_file_paths"),
                            GetStringArray(item, "allowed_tools"),
                            GetInt(item, "max_tool_calls") ?? 1));
                }
            }

            plan = new PrWideReviewPlan(planId, concerns, changedAreas, tasks, noInvestigationReason);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseInvestigationResult(
        string? responseText,
        PrWideInvestigationTask task,
        IReadOnlyList<PrWideToolUsage> toolUsage,
        out PrWideInvestigationResult? result)
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

            var evidence = new List<EvidenceItem>();
            if (root.TryGetProperty("evidence", out var evidenceEl) && evidenceEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in evidenceEl.EnumerateArray())
                {
                    var kind = GetString(item, "kind");
                    var summary = GetString(item, "summary");
                    if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(summary))
                    {
                        continue;
                    }

                    evidence.Add(
                        new EvidenceItem(
                            kind,
                            summary,
                            GetString(item, "source_id")));
                }
            }

            var candidates = new List<PrWideCandidateFinding>();
            if (root.TryGetProperty("candidate_findings", out var candidatesEl) && candidatesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in candidatesEl.EnumerateArray())
                {
                    var id = GetString(item, "id");
                    var message = GetString(item, "message");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(message))
                    {
                        continue;
                    }

                    var confidence = ParseConfidence(item.TryGetProperty("confidence", out var confEl) ? confEl : default);
                    var supportingFiles = GetStringArray(item, "supporting_files");
                    candidates.Add(
                        new PrWideCandidateFinding(
                            id,
                            message,
                            GetString(item, "category") ?? CandidateReviewFinding.CrossCuttingCategory,
                            confidence,
                            new EvidenceReference(
                                [], supportingFiles, supportingFiles.Count >= 2 ? EvidenceReference.ResolvedState : EvidenceReference.PartialState,
                                "pr_wide_investigation"),
                            supportingFiles.Count > 0 ? supportingFiles : task.SeedFilePaths));
                }
            }

            result = new PrWideInvestigationResult(
                task.Id,
                GetString(root, "status") ?? "completed",
                evidence,
                candidates,
                toolUsage,
                GetBool(root, "degraded") ?? false);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseSynthesizedCandidates(
        string? responseText,
        out IReadOnlyList<PrWideCandidateFinding> findings)
    {
        if (TryParseSynthesisResult(responseText, out var synthesis) && synthesis is not null)
        {
            findings = synthesis.CandidateFindings;
            return true;
        }

        findings = [];
        return false;
    }

    internal static bool TryParseSynthesisResult(
        string? responseText,
        out PrWideSynthesisResult? synthesis)
    {
        synthesis = null;
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(StripMarkdownCodeFences(responseText));
            var root = doc.RootElement;
            if (!root.TryGetProperty("candidate_findings", out var candidatesEl) ||
                candidatesEl.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var results = new List<PrWideCandidateFinding>();
            foreach (var item in candidatesEl.EnumerateArray())
            {
                var id = GetString(item, "id");
                var message = GetString(item, "message");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                var supportingFiles = GetStringArray(item, "supporting_files");
                var evidenceResolutionState = GetString(item, "evidence_resolution_state")
                                              ?? GetString(item, "evidenceResolutionState")
                                              ?? (supportingFiles.Count >= 2 ? EvidenceReference.ResolvedState : EvidenceReference.PartialState);
                var evidenceSource = GetString(item, "evidence_source")
                                     ?? GetString(item, "evidenceSource")
                                     ?? "pr_wide_synthesis";
                results.Add(
                    new PrWideCandidateFinding(
                        id,
                        message,
                        GetString(item, "category") ?? CandidateReviewFinding.CrossCuttingCategory,
                        ParseConfidence(item.TryGetProperty("confidence", out var confEl) ? confEl : default),
                        new EvidenceReference(
                            GetStringArray(item, "supporting_finding_ids"),
                            supportingFiles,
                            evidenceResolutionState,
                            evidenceSource),
                        supportingFiles,
                        ParseSeverity(item),
                        GetString(item, "file_path") ?? GetString(item, "anchor_file_path"),
                        GetInt(item, "line_number") ?? GetInt(item, "anchor_line_number"),
                        GetString(item, "candidate_summary_text")));
            }

            synthesis = new PrWideSynthesisResult(
                GetString(root, "summary") ?? BuildFallbackSummary(results),
                results);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildFallbackSummary(IReadOnlyList<PrWideCandidateFinding> findings)
    {
        return findings.Count == 0
            ? "No PR-wide candidate findings were synthesized."
            : $"PR-wide synthesis produced {findings.Count} candidate finding(s).";
    }

    private static ConfidenceScore ParseConfidence(JsonElement element)
    {
        var concern = element.ValueKind == JsonValueKind.Object
            ? GetString(element, "concern") ?? "cross_file_reasoning"
            : "cross_file_reasoning";
        var score = element.ValueKind == JsonValueKind.Object
            ? GetInt(element, "score") ?? 70
            : 70;
        return new ConfidenceScore(concern, score);
    }

    private static CommentSeverity ParseSeverity(JsonElement element)
    {
        var severity = GetString(element, "severity");
        return severity?.Trim().ToLowerInvariant() switch
        {
            "error" => CommentSeverity.Error,
            "suggestion" => CommentSeverity.Suggestion,
            "info" => CommentSeverity.Info,
            _ => CommentSeverity.Warning,
        };
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arrayEl) || arrayEl.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return arrayEl.EnumerateArray()
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
