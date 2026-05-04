// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;

internal sealed partial class AiCommentRelevanceAmbiguityEvaluator(ILogger<AiCommentRelevanceAmbiguityEvaluator> logger) : ICommentRelevanceAmbiguityEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<CommentRelevanceAmbiguityEvaluationResult> EvaluateAsync(
        CommentRelevanceFilterRequest request,
        IReadOnlyList<ReviewComment> comments,
        CancellationToken ct = default)
    {
        if (comments.Count == 0)
        {
            return new CommentRelevanceAmbiguityEvaluationResult([], true, null, [], [], null);
        }

        try
        {
            var chatClient = request.ReviewContext.DefaultReviewChatClient;
            var modelId = request.ReviewContext.DefaultReviewModelId;
            if (chatClient is null || string.IsNullOrWhiteSpace(modelId))
            {
                return CommentRelevanceAmbiguityEvaluationResult.Unavailable(
                    ["comment_relevance_evaluator"],
                    ["ambiguous_survivors_left_unchanged"],
                    "Comment relevance evaluator default review runtime is unavailable; ambiguous survivors were kept unchanged.");
            }

            var prompt = BuildPrompt(request, comments);
            var response = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, BuildSystemPrompt()),
                    new ChatMessage(ChatRole.User, prompt),
                ],
                new ChatOptions { ModelId = modelId, Temperature = request.ReviewContext.Temperature },
                ct);

            var parsed = ParseResponse(response.Text ?? string.Empty, comments);
            if (parsed is null)
            {
                LogParseFailed(logger);
                return CommentRelevanceAmbiguityEvaluationResult.Unavailable(
                    ["comment_relevance_evaluator"],
                    ["ambiguous_survivors_left_unchanged"],
                    "Comment relevance evaluator returned an unparseable response.");
            }

            var tokenUsage = new FilterAiTokenUsage(
                "hybrid-v1",
                request.FilePath,
                response.Usage?.InputTokenCount ?? 0,
                response.Usage?.OutputTokenCount ?? 0,
                AiConnectionModelCategory.Default,
                modelId);

            return new CommentRelevanceAmbiguityEvaluationResult(
                parsed,
                true,
                tokenUsage,
                [],
                [],
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogEvaluationFailed(logger, ex);
            return CommentRelevanceAmbiguityEvaluationResult.Unavailable(
                ["comment_relevance_evaluator"],
                ["ambiguous_survivors_left_unchanged"],
                "Comment relevance evaluator failed; ambiguous survivors were kept unchanged.");
        }
    }

    internal static string BuildSystemPrompt()
    {
        return "You are a code-review comment relevance adjudicator. For each ambiguous comment, decide Keep or Discard. Return only valid JSON.";
    }

    internal static string BuildPrompt(CommentRelevanceFilterRequest request, IReadOnlyList<ReviewComment> comments)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"File: {request.FilePath}");
        sb.AppendLine("Return JSON with this shape: {\"decisions\":[{\"index\":0,\"decision\":\"Keep\"|\"Discard\",\"reasonCodes\":[\"reason_code\"]}]}");
        sb.AppendLine("Discard only comments that are unsupported, summary-only, misanchored, or otherwise low relevance for an actionable review thread.");
        sb.AppendLine();

        for (var i = 0; i < comments.Count; i++)
        {
            var comment = comments[i];
            sb.AppendLine($"[{i}] severity={comment.Severity.ToString().ToLowerInvariant()} line={comment.LineNumber?.ToString() ?? "null"}");
            sb.AppendLine(comment.Message);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    internal static IReadOnlyList<CommentRelevanceFilterDecision>? ParseResponse(
        string responseText,
        IReadOnlyList<ReviewComment> comments)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<EvaluatorResponse>(responseText, JsonOptions);
            if (parsed?.Decisions is null || parsed.Decisions.Count != comments.Count)
            {
                return null;
            }

            var byIndex = parsed.Decisions
                .GroupBy(item => item.Index)
                .ToDictionary(group => group.Key, group => group.Last());

            var decisions = new List<CommentRelevanceFilterDecision>(comments.Count);
            for (var i = 0; i < comments.Count; i++)
            {
                if (!byIndex.TryGetValue(i, out var item) || string.IsNullOrWhiteSpace(item.Decision))
                {
                    return null;
                }

                var normalizedDecision = string.Equals(item.Decision, CommentRelevanceFilterDecision.DiscardDecision, StringComparison.OrdinalIgnoreCase)
                    ? CommentRelevanceFilterDecision.DiscardDecision
                    : CommentRelevanceFilterDecision.KeepDecision;
                var reasonCodes = item.ReasonCodes?.Where(code => !string.IsNullOrWhiteSpace(code)).ToArray() ?? [];

                decisions.Add(
                    new CommentRelevanceFilterDecision(
                        normalizedDecision,
                        comments[i],
                        reasonCodes,
                        CommentRelevanceFilterDecision.AiAdjudicationSource));
            }

            return decisions.AsReadOnly();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(EventId = 4140, Level = LogLevel.Warning, Message = "Comment relevance evaluator returned an unparseable response")]
    private static partial void LogParseFailed(ILogger logger);

    [LoggerMessage(EventId = 4141, Level = LogLevel.Warning, Message = "Comment relevance evaluator call failed")]
    private static partial void LogEvaluationFailed(ILogger logger, Exception ex);

    private sealed class EvaluatorResponse
    {
        [JsonPropertyName("decisions")]
        public List<EvaluatorDecision>? Decisions { get; set; }
    }

    private sealed class EvaluatorDecision
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("decision")]
        public string? Decision { get; set; }

        [JsonPropertyName("reasonCodes")]
        public string[]? ReasonCodes { get; set; }
    }
}
