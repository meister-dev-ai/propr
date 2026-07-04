// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     LLM self-reflection re-ranking stage for per-file review results. Sends candidate findings to the LLM
///     for importance scoring (0–10) and filters/orders by the returned scores. Falls back to deterministic
///     <see cref="FileByFileImportanceRankingStage" /> ordering on any LLM failure. Replaces the deterministic
///     ranking stage in Balanced and Assertive profiles.
/// </summary>
internal sealed class FileByFileSelfReflectionRankingStage(
    AiReviewOptions options,
    ILogger<FileByFileSelfReflectionRankingStage> logger,
    IProtocolRecorder? protocolRecorder = null) : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "file-by-file.self-reflection-ranking";
    private readonly ILogger<FileByFileSelfReflectionRankingStage> _logger = logger;

    private readonly AiReviewOptions _options = options;
    private readonly IProtocolRecorder? _protocolRecorder = protocolRecorder;

    public FileByFileSelfReflectionRankingStage()
        : this(new AiReviewOptions(), NullLogger<FileByFileSelfReflectionRankingStage>.Instance)
    {
    }

    public string StageId => StageIdConstant;

    public async Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        if (context.ReviewResult is null || context.ReviewResult.Comments.Count <= 1)
        {
            return context;
        }

        var chatClient = context.FileReviewContext.TierChatClient ?? context.FileReviewContext.DefaultReviewChatClient;
        if (chatClient is null)
        {
            return this.ApplyDeterministicRanking(context);
        }

        var comments = context.ReviewResult.Comments.ToList();
        var candidateCount = comments.Count;

        try
        {
            var systemPrompt = ReviewPrompts.BuildImportanceRankingSystemPrompt(context.FileReviewContext);
            var userMessage = ReviewPrompts.BuildImportanceRankingUserMessage(comments, this._options);

            var response = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userMessage),
                ],
                new ChatOptions
                {
                    ModelId = context.FileReviewContext.ModelId ?? this._options.ModelId,
                    Temperature = context.FileReviewContext.Temperature,
                },
                cancellationToken);

            var responseText = response.Text ?? string.Empty;
            var scores = ParseScores(responseText);

            if (scores is null || scores.Count == 0)
            {
                this._logger.LogWarning("Self-reflection ranking returned unparseable response for file context; falling back to deterministic ranking");
                await this.RecordEventAsync(context, candidateCount, 0, false, cancellationToken);
                return this.ApplyDeterministicRanking(context);
            }

            // Build a lookup: index → (importance, keep)
            var scoreByIndex = scores.ToDictionary(s => s.Index);
            var ranked = this.BuildRankedComments(comments, scoreByIndex);

            var finalComments = ranked
                .OrderByDescending(e => e.Importance)
                .ThenByDescending(e => e.Comment.Severity)
                .ThenBy(e => e.Comment.LineNumber ?? int.MaxValue)
                .Take(this._options.ImportanceRankingKeepTopN)
                .Select(e => e.Comment)
                .ToList();

            var keptCount = finalComments.Count;
            await this.RecordEventAsync(context, candidateCount, keptCount, true, cancellationToken);

            return finalComments.Count == context.ReviewResult.Comments.Count
                ? context
                : context with { ReviewResult = context.ReviewResult with { Comments = finalComments } };
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "Self-reflection ranking failed; falling back to deterministic ranking");
            await this.RecordEventAsync(context, candidateCount, 0, false, cancellationToken);
            return this.ApplyDeterministicRanking(context);
        }
    }

    private List<(ReviewComment Comment, int Importance)> BuildRankedComments(
        List<ReviewComment> comments,
        Dictionary<int, RankingScore> scoreByIndex)
    {
        var ranked = new List<(ReviewComment Comment, int Importance)>();
        for (var i = 0; i < comments.Count; i++)
        {
            if (scoreByIndex.TryGetValue(i, out var score))
            {
                if (score.Keep && score.Importance >= this._options.ImportanceRankingMinScore)
                {
                    ranked.Add((comments[i], score.Importance));
                }

                continue;
            }

            // LLM didn't score this entry — include it with deterministic score
            var detScore = FileByFileImportanceRankingStage.ScoreComment(comments[i]);
            if (detScore >= this._options.ImportanceRankingMinScore)
            {
                ranked.Add((comments[i], detScore));
            }
        }

        return ranked;
    }

    private PerFileReviewContext ApplyDeterministicRanking(PerFileReviewContext context)
    {
        if (context.ReviewResult is null || context.ReviewResult.Comments.Count <= 1)
        {
            return context;
        }

        var rankedComments = context.ReviewResult.Comments
            .Select(comment => new
            {
                Comment = comment,
                Score = FileByFileImportanceRankingStage.ScoreComment(comment),
            })
            .Where(entry => entry.Score >= this._options.ImportanceRankingMinScore)
            .OrderByDescending(entry => entry.Score)
            .ThenByDescending(entry => entry.Comment.Severity)
            .ThenBy(entry => entry.Comment.LineNumber ?? int.MaxValue)
            .Take(this._options.ImportanceRankingKeepTopN)
            .Select(entry => entry.Comment)
            .ToList();

        return rankedComments.Count == context.ReviewResult.Comments.Count
            ? context
            : context with { ReviewResult = context.ReviewResult with { Comments = rankedComments } };
    }

    private static List<RankingScore>? ParseScores(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (!doc.RootElement.TryGetProperty("scores", out var scoresEl) ||
                scoresEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<RankingScore>();
            foreach (var item in scoresEl.EnumerateArray())
            {
                if (!item.TryGetProperty("index", out var idxEl) || idxEl.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var index = idxEl.GetInt32();
                var importance = item.TryGetProperty("importance", out var impEl) ? impEl.GetInt32() : 0;
                var keep = !item.TryGetProperty("keep", out var keepEl) || keepEl.GetBoolean();

                result.Add(new RankingScore(index, importance, keep));
            }

            return result.Count > 0 ? result : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task RecordEventAsync(
        PerFileReviewContext context,
        int candidateCount,
        int keptCount,
        bool usedLlm,
        CancellationToken cancellationToken)
    {
        var recorder = this._protocolRecorder ?? context.FileReviewContext.ProtocolRecorder;
        if (context.ProtocolId.HasValue && recorder is not null)
        {
            try
            {
                await recorder.RecordReviewStrategyEventAsync(
                    context.ProtocolId.Value,
                    ReviewProtocolEventNames.ImportanceRankingApplied,
                    JsonSerializer.Serialize(new { candidateCount, keptCount, usedLlm }),
                    null,
                    null,
                    cancellationToken);
            }
            catch
            {
                // Protocol recording is best-effort.
            }
        }
    }

    private sealed record RankingScore(int Index, int Importance, bool Keep);
}
