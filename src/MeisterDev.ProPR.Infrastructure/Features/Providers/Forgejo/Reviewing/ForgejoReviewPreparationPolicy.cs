// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;

/// <summary>Prepares Forgejo inline comments against inserted diff lines.</summary>
internal sealed class ForgejoReviewPreparationPolicy : CodeReviewPreparationPolicyBase
{
    public override ScmProvider Provider => ScmProvider.Forgejo;

    public override PreparedReviewResult PrepareResult(PullRequest pullRequest, ReviewResult result)
    {
        if (result.Comments.Count == 0)
        {
            return new(result);
        }

        var insertedLines = ReviewDiffAnchors.BuildInsertedLineLookup(pullRequest.ChangedFiles);
        var comments = new List<ReviewComment>(result.Comments.Count);
        var downgraded = 0;
        foreach (var comment in result.Comments)
        {
            if (!string.IsNullOrWhiteSpace(comment.FilePath) && comment.LineNumber is > 0
                                                             && (!insertedLines.TryGetValue(ReviewDiffAnchors.NormalizePath(comment.FilePath), out var lines)
                                                                 || !lines.Contains(comment.LineNumber.Value)))
            {
                comments.Add(comment.AsPullRequestLevel($"{ReviewDiffAnchors.NormalizePath(comment.FilePath)}:L{comment.LineNumber.Value}: {comment.Message}"));
                downgraded++;
            }
            else
            {
                comments.Add(comment);
            }
        }

        return new(downgraded == 0 ? result : result with { Comments = comments.AsReadOnly() }, downgraded);
    }
}
