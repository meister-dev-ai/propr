// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;

internal static class ReviewCommentProcessing
{
    internal static ReviewComment CreateReviewComment(string? filePath, int? lineNumber, CommentSeverity severity, string message)
    {
        return new ReviewComment(filePath, NormalizeLineNumber(lineNumber), severity, message);
    }

    internal static int? NormalizeLineNumber(int? lineNumber)
    {
        return lineNumber is > 0 ? lineNumber : null;
    }

    internal static ReviewResult StripInfoComments(ReviewResult result)
    {
        if (result.Comments.Count == 0)
        {
            return result;
        }

        var filtered = result.Comments
            .Where(c => c.Severity != CommentSeverity.Info)
            .ToList()
            .AsReadOnly();

        return filtered.Count == result.Comments.Count
            ? result
            : result with { Comments = filtered };
    }

    internal static ReviewResult ApplyConfidenceFloor(ReviewResult result, int? finalConfidence, AiReviewOptions opts)
    {
        if (finalConfidence is null || result.Comments.Count == 0)
        {
            return result;
        }

        var confidence = finalConfidence.Value;
        var adjusted = result.Comments
            .Select(c =>
            {
                var severity = c.Severity;
                if (severity == CommentSeverity.Error && confidence < opts.ConfidenceFloorError)
                {
                    severity = CommentSeverity.Warning;
                }
                else if (severity == CommentSeverity.Warning && confidence < opts.ConfidenceFloorWarning)
                {
                    severity = CommentSeverity.Suggestion;
                }

                return severity == c.Severity ? c : CreateReviewComment(c.FilePath, c.LineNumber, severity, c.Message);
            })
            .ToList()
            .AsReadOnly();

        return adjusted.SequenceEqual(result.Comments)
            ? result
            : result with { Comments = adjusted };
    }
}
