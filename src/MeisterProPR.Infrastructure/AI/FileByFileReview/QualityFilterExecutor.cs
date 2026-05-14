// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AI.FileByFileReview;

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
    public static List<ReviewComment> ParseResponse(string responseText)
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
                var message = item.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
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

                result.Add(FileByFileReviewOrchestrator.CreateReviewComment(filePath, lineNumber, severity, message));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
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

            var parsed = ParseResponse(response.Text ?? string.Empty);
            var kept = parsed.Count > 0 ? parsed : comments;

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
