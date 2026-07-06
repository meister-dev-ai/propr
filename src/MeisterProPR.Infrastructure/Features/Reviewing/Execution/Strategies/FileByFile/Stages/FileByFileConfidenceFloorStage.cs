// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

internal sealed class FileByFileConfidenceFloorStage(AiReviewOptions options, IProtocolRecorder? protocolRecorder = null)
    : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "file-by-file.confidence-floor";

    public string StageId => StageIdConstant;

    public async Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        if (context.ReviewResult is null)
        {
            return context;
        }

        var finalConfidence = context.FileReviewContext.LoopMetrics?.FinalConfidence;
        var result = ReviewCommentProcessing.ApplyConfidenceFloor(context.ReviewResult, finalConfidence, options);
        await PipelineStageTracing.RecordConfidenceDowngradesAsync(
            protocolRecorder, context.ProtocolId, context.ReviewResult, result, finalConfidence, cancellationToken).ConfigureAwait(false);
        return context with { ReviewResult = result };
    }
}
