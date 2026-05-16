// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Options;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;

internal sealed class AgenticConfidenceFloorStage(AiReviewOptions options) : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "agentic.confidence-floor";

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

internal sealed class AgenticSpeculativeCommentFilterStage : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "agentic.filter-speculative";

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

internal sealed class AgenticInfoCommentStripStage : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "agentic.strip-info";

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

internal sealed class AgenticVagueSuggestionFilterStage : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "agentic.filter-vague-suggestions";

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
