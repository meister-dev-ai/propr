// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Runs the cross-file quality-filter AI pass and parses its structured output. This executor is responsible
///     only for pruning or rewriting the synthesized comment set after deduplication; it never mutates persisted
///     per-file results and always fails open by returning the original comments when the AI step cannot produce
///     a usable filtered list.
/// </summary>
internal sealed class QualityFilterExecutor(
    AiReviewOptions options,
    ILogger<FileByFileReviewOrchestrator> logger)
{
    public static List<ReviewComment> ParseResponse(
        string responseText, IReadOnlyList<ReviewComment>? originals = null, FindingTypeVocabulary? vocabulary = null)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (!doc.RootElement.TryGetProperty("comments", out var commentsEl) ||
                commentsEl.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<ReviewComment>();
            foreach (var item in commentsEl.EnumerateArray())
            {
                var comment = TryParseComment(item, vocabulary);
                if (comment is not null)
                {
                    if (originals is null)
                    {
                        result.Add(comment);
                        continue;
                    }

                    var sources = ResolveSources(item, comment, originals);
                    if (sources is null || !HasCompatibleFixes(sources))
                    {
                        return [];
                    }

                    result.Add(PreserveSourceEvidence(comment, sources, item));
                }
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool HasCompatibleFixes(IReadOnlyList<ReviewComment> sources)
    {
        if (sources.Count == 1 || sources.All(source => source.SuggestedFix is null))
        {
            return true;
        }

        var first = sources[0];
        return first.SuggestedFix is not null && sources.All(source => source.SuggestedFix == first.SuggestedFix
                                                                       && source.FilePath == first.FilePath && source.LineNumber == first.LineNumber);
    }

    private static IReadOnlyList<ReviewComment>? ResolveSources(JsonElement item, ReviewComment parsed, IReadOnlyList<ReviewComment> originals)
    {
        if (!item.TryGetProperty("source_finding_ids", out var sourceIds))
        {
            var unchanged = originals.Where(original => original.FilePath == parsed.FilePath && original.LineNumber == parsed.LineNumber
                                                                                             && original.Message == parsed.Message).Take(2).ToList();
            return unchanged.Count == 1 ? unchanged : null;
        }

        if (sourceIds.ValueKind != JsonValueKind.Array || sourceIds.GetArrayLength() == 0 || sourceIds.GetArrayLength() > originals.Count)
        {
            return null;
        }

        var knownIds = originals.Select((original, index) => new { Id = ReviewPrompts.QualityFilterSourceId(index), Original = original })
            .ToDictionary(source => source.Id, source => source.Original, StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in sourceIds.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.String || id.GetString() is not { } value || !knownIds.ContainsKey(value) || !selected.Add(value))
            {
                return null;
            }
        }

        return knownIds.Where(source => selected.Contains(source.Key)).Select(source => source.Value).ToList();
    }

    private static ReviewComment PreserveSourceEvidence(ReviewComment parsed, IReadOnlyList<ReviewComment> sources, JsonElement rawFinding)
    {
        var anchorMatches = sources.Where(source => source.FilePath == parsed.FilePath && source.LineNumber == parsed.LineNumber).Take(2).ToList();
        var anchorSource = anchorMatches.Count == 1 ? anchorMatches[0] : sources[0];
        var strongestSeverity = sources.MaxBy(source => source.Severity.Rank())!.Severity;
        var rewordedSource = anchorSource.WithMessage(parsed.Message).WithAnchorAndSeverity(parsed.FilePath, parsed.LineNumber, strongestSeverity);
        var guarded = sources.Count > 1 && strongestSeverity == CommentSeverity.Error
            ? parsed.WithAnchorAndSeverity(parsed.FilePath, parsed.LineNumber, CommentSeverity.Error)
            : parsed;
        var retained = FindingRiskAssessmentParser.PreserveUnchangedEvidence(guarded, [rewordedSource], rawFinding);
        return retained with
        {
            RiskAssessment = sources.Count == 1 ? retained.RiskAssessment : ConsolidateSourceRisk(sources),
            ProducedWithRepositoryTools = sources.Any(source => source.ProducedWithRepositoryTools == false) ? false
            : sources.All(source => source.ProducedWithRepositoryTools == true) ? true
            : null,
            FindingClassification = FindingTypeClassificationPreserver.Preserve(
                parsed.FindingClassification, sources.Count == 1 ? sources[0].FindingClassification : null,
                sources.Select(source => source.FindingClassification)),
        };
    }

    private static FindingRiskAssessment? ConsolidateSourceRisk(IReadOnlyList<ReviewComment> sources)
    {
        var highestSupported = sources.Select(source => source.RiskAssessment)
            .Where(risk => risk?.Severity is > FindingRiskSeverity.Unknown).MaxBy(risk => risk!.Severity);
        if (highestSupported is not null)
        {
            return ReviewFindingConfidence.Consolidate(highestSupported, sources.Select(source => source.RiskAssessment));
        }

        var first = sources[0].RiskAssessment;
        return sources.All(source => source.RiskAssessment == first) ? first : null;
    }

    private static ReviewComment? TryParseComment(JsonElement item, FindingTypeVocabulary? vocabulary)
    {
        var message = item.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        string? filePath = null;
        if (item.TryGetProperty("file_path", out var fpEl) && fpEl.ValueKind == JsonValueKind.String)
        {
            filePath = fpEl.GetString();
        }

        int? lineNumber = null;
        if (item.TryGetProperty("line_number", out var lnEl) && lnEl.ValueKind == JsonValueKind.Number)
        {
            lineNumber = FileByFileReviewOrchestrator.NormalizeLineNumber(lnEl.GetInt32());
        }

        var severity = CommentSeverity.Warning;
        if (item.TryGetProperty("severity", out var sevEl))
        {
            severity = sevEl.GetString()?.ToLowerInvariant() switch
            {
                "error" => CommentSeverity.Error,
                "suggestion" => CommentSeverity.Suggestion,
                "info" => CommentSeverity.Info,
                _ => CommentSeverity.Warning,
            };
        }

        return FileByFileReviewOrchestrator.CreateReviewComment(filePath, lineNumber, severity, message) with
        {
            RiskAssessment = FindingRiskAssessmentParser.ParseFinding(item),
            FindingClassification = FindingTypeClassificationParser.ParseFinding(item, vocabulary),
            SuggestedFix = ReviewSuggestedFixValidator.Parse(item),
        };
    }

    public async Task<List<ReviewComment>> ApplyAsync(
        Guid jobId,
        List<ReviewComment> comments,
        ReviewSystemContext reviewContext,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Starting quality filter for {CommentCount} comments in job {JobId}", comments.Count, jobId);

            var systemPrompt = ReviewPrompts.BuildQualityFilterSystemPrompt(reviewContext);
            var userMessage = ReviewPrompts.BuildQualityFilterUserMessage(comments);

            var response = await effectiveClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userMessage),
                ],
                new ChatOptions { ModelId = reviewContext.ModelId ?? options.ModelId, Temperature = reviewContext.Temperature },
                ct);

            var parsed = ParseResponse(response.Text ?? string.Empty, comments, reviewContext.FindingTypeVocabulary);
            var kept = parsed.Count > 0
                ? parsed
                : comments;

            logger.LogInformation(
                "Quality filter completed for job {JobId}: {Before} -> {After} comments",
                jobId,
                comments.Count,
                kept.Count);
            return kept;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Quality filter failed for job {JobId}; using unfiltered comments", jobId);
            return comments;
        }
    }
}
