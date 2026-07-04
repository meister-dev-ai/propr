// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Parses structured synthesis responses returned by the cross-file synthesis model.
///     This parser is intentionally pure: it strips markdown fences, extracts the synthesized
///     narrative summary, and materializes any structured cross-cutting findings carried in the
///     JSON payload without invoking external services or protocol side effects.
/// </summary>
internal static class SynthesisResponseParser
{
    public static bool TryParse(
        string? responseText,
        out string summary,
        out IReadOnlyList<CandidateReviewFinding> crossCuttingComments)
    {
        summary = string.Empty;
        crossCuttingComments = [];

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        var trimmed = StripMarkdownCodeFences(responseText);

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (!doc.RootElement.TryGetProperty("summary", out var summaryEl))
            {
                return false;
            }

            summary = summaryEl.ValueKind == JsonValueKind.String
                ? summaryEl.GetString() ?? string.Empty
                : summaryEl.GetRawText();
            crossCuttingComments = ParseCrossCuttingConcerns(trimmed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool LooksLikeJsonObject(string text)
    {
        return StripMarkdownCodeFences(text).StartsWith("{", StringComparison.Ordinal);
    }

    public static string StripMarkdownCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline >= 0)
        {
            trimmed = trimmed[(firstNewline + 1)..];
        }
        else
        {
            var braceStart = trimmed.IndexOf('{');
            if (braceStart >= 0)
            {
                trimmed = trimmed[braceStart..];
            }
        }

        var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence >= 0)
        {
            trimmed = trimmed[..closingFence];
        }

        return trimmed.Trim();
    }

    private static IReadOnlyList<CandidateReviewFinding> ParseCrossCuttingConcerns(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(StripMarkdownCodeFences(responseText));
            if (!doc.RootElement.TryGetProperty("cross_cutting_concerns", out var concernsEl) ||
                concernsEl.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<CandidateReviewFinding>();
            foreach (var item in concernsEl.EnumerateArray())
            {
                var finding = TryParseCrossCuttingConcern(item, result.Count + 1);
                if (finding is not null)
                {
                    result.Add(finding);
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static CandidateReviewFinding? TryParseCrossCuttingConcern(JsonElement item, int ordinal)
    {
        var message = item.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var severity = CommentSeverity.Warning;
        if (item.TryGetProperty("severity", out var sevEl))
        {
            var sevStr = sevEl.GetString() ?? string.Empty;
            severity = sevStr.ToLowerInvariant() switch
            {
                "error" => CommentSeverity.Error,
                "info" => CommentSeverity.Info,
                "suggestion" => CommentSeverity.Suggestion,
                _ => CommentSeverity.Warning,
            };
        }

        var category = item.TryGetProperty("category", out var categoryEl)
            ? categoryEl.GetString()
            : null;
        category = string.IsNullOrWhiteSpace(category)
            ? CandidateReviewFinding.CrossCuttingCategory
            : category;

        var summaryText = item.TryGetProperty("candidateSummaryText", out var summaryEl)
            ? summaryEl.GetString()
            : null;
        var evidence = ParseEvidenceReference(item);

        return new CandidateReviewFinding(
            $"finding-cc-unassigned-{ordinal:D3}",
            new CandidateFindingProvenance(
                CandidateFindingProvenance.SynthesizedCrossCuttingOrigin,
                "synthesis"),
            severity,
            message,
            category,
            evidence: evidence,
            candidateSummaryText: summaryText);
    }

    private static EvidenceReference ParseEvidenceReference(JsonElement item)
    {
        var supportingFindingIds = item.TryGetProperty("supportingFindingIds", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array
            ? idsEl.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray()
            : [];

        var supportingFiles = item.TryGetProperty("supportingFiles", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array
            ? filesEl.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray()
            : [];

        var evidenceResolutionState = item.TryGetProperty("evidenceResolutionState", out var stateEl)
            ? stateEl.GetString()
            : null;
        var evidenceSource = item.TryGetProperty("evidenceSource", out var sourceEl)
            ? sourceEl.GetString()
            : null;

        return new EvidenceReference(
            supportingFindingIds,
            supportingFiles,
            string.IsNullOrWhiteSpace(evidenceResolutionState) ? EvidenceReference.MissingState : evidenceResolutionState,
            string.IsNullOrWhiteSpace(evidenceSource) ? "synthesis_payload" : evidenceSource);
    }
}
