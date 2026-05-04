// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;

internal sealed class PassThroughCommentRelevanceFilter : ICommentRelevanceFilter
{
    public string ImplementationId => "pass-through-v1";

    public string ImplementationVersion => "1.0.0";

    public Task<CommentRelevanceFilterResult> FilterAsync(
        CommentRelevanceFilterRequest request,
        CancellationToken ct = default)
    {
        var decisions = request.Comments
            .Select(comment => new CommentRelevanceFilterDecision(
                CommentRelevanceFilterDecision.KeepDecision,
                comment,
                [],
                CommentRelevanceFilterDecision.DeterministicScreeningSource))
            .ToList()
            .AsReadOnly();

        return Task.FromResult(
            new CommentRelevanceFilterResult(
                this.ImplementationId,
                this.ImplementationVersion,
                request.FilePath,
                request.Comments.Count,
                decisions));
    }
}
