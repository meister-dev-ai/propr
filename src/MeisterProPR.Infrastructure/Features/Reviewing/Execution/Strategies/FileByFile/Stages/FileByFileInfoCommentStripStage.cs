// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

internal sealed class FileByFileInfoCommentStripStage(IProtocolRecorder? protocolRecorder = null)
    : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "file-by-file.strip-info";

    public string StageId => StageIdConstant;

    public async Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        if (context.ReviewResult is null)
        {
            return context;
        }

        var stripped = ReviewCommentProcessing.StripInfoComments(context.ReviewResult);
        await PipelineStageTracing.RecordInfoStrippedAsync(protocolRecorder, context.ProtocolId, context.ReviewResult, stripped, cancellationToken)
            .ConfigureAwait(false);
        return context with { ReviewResult = stripped };
    }
}
