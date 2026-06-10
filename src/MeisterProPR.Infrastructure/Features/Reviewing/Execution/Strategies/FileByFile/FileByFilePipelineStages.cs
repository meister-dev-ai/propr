// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

internal sealed class FileByFileConfidenceFloorStage(AiReviewOptions options) : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "file-by-file.confidence-floor";

    public string StageId => StageIdConstant;

    public Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        var result = context.ReviewResult is null
            ? null
            : ReviewCommentProcessing.ApplyConfidenceFloor(
                context.ReviewResult,
                context.FileReviewContext.LoopMetrics?.FinalConfidence,
                options);

        return Task.FromResult(context with { ReviewResult = result });
    }
}

internal sealed class FileByFileSpeculativeCommentFilterStage : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "file-by-file.filter-speculative";

    public string StageId => StageIdConstant;

    public Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(
            context with
            {
                ReviewResult = context.ReviewResult is null ? null : ReviewCommentProcessing.FilterSpeculativeComments(context.ReviewResult),
            });
    }
}

internal sealed class FileByFileInfoCommentStripStage : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "file-by-file.strip-info";

    public string StageId => StageIdConstant;

    public Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(
            context with
            {
                ReviewResult = context.ReviewResult is null ? null : ReviewCommentProcessing.StripInfoComments(context.ReviewResult),
            });
    }
}

internal sealed class FileByFileVagueSuggestionFilterStage : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "file-by-file.filter-vague-suggestions";

    public string StageId => StageIdConstant;

    public Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(
            context with
            {
                ReviewResult = context.ReviewResult is null ? null : ReviewCommentProcessing.FilterVagueSuggestions(context.ReviewResult),
            });
    }
}

internal sealed class FileByFileImportanceRankingStage : IReviewPipelineStage<PerFileReviewContext>
{
    public const string StageIdConstant = "file-by-file.importance-ranking";
    private readonly AiReviewOptions _options;

    public FileByFileImportanceRankingStage()
        : this(new AiReviewOptions())
    {
    }

    public FileByFileImportanceRankingStage(AiReviewOptions options)
    {
        this._options = options;
    }

    public string StageId => StageIdConstant;

    public Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        if (context.ReviewResult is null || context.ReviewResult.Comments.Count <= 1)
        {
            return Task.FromResult(context);
        }

        var rankedComments = context.ReviewResult.Comments
            .Select(comment => new
            {
                Comment = comment,
                Score = Score(comment),
            })
            .Where(entry => entry.Score >= this._options.ImportanceRankingMinScore)
            .OrderByDescending(entry => entry.Score)
            .ThenByDescending(entry => entry.Comment.Severity)
            .ThenBy(entry => entry.Comment.LineNumber ?? int.MaxValue)
            .Take(this._options.ImportanceRankingKeepTopN)
            .Select(entry => entry.Comment)
            .ToList();

        return Task.FromResult(
            rankedComments.Count == context.ReviewResult.Comments.Count
                ? context
                : context with { ReviewResult = context.ReviewResult with { Comments = rankedComments } });
    }

    private static int Score(ReviewComment comment)
    {
        return ScoreComment(comment);
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Computes a deterministic 1-10 importance score for a comment based on severity and keyword signals.
    ///     Shared with <c>FileByFileSelfReflectionRankingStage</c> as a fallback and feature input.
    /// </summary>
    internal static int ScoreComment(ReviewComment comment)
    {
        var message = comment.Message;
        var severityScore = comment.Severity switch
        {
            CommentSeverity.Error => 10,
            CommentSeverity.Warning => 7,
            CommentSeverity.Suggestion => 4,
            _ => 1,
        };

        if (ContainsAnyStatic(
                message, "security", "auth", "authorization", "token", "secret", "credential", "permission", "bypass", "race", "deadlock", "concurrency",
                "injection", "vulnerability"))
        {
            severityScore += 2;
        }

        if (ContainsAnyStatic(message, "missing", "break", "broken", "fails", "incorrect", "invalid", "unsafe", "exposed", "lost", "stale"))
        {
            severityScore += 1;
        }

        return Math.Min(severityScore, 10);
    }

    private static bool ContainsAnyStatic(string text, params string[] needles)
    {
        return needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
