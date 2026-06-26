// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Options;

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
