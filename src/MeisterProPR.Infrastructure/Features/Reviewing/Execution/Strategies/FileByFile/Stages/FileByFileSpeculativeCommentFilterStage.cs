// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

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
