// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Screening;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Language-robust replacement for the English phrase-list hedge/vague filters on the file-by-file path. When
///     the client enables language-robust screening, each comment is classified by <see cref="ISemanticCommentScreener" />
///     (embedding similarity, multilingual) and demoted — never deleted: firm comments are kept, every non-Firm
///     classification (hedged or vague, at any severity) folds into the review summary. The screening semantics live
///     in <see cref="SemanticScreeningApplier" />, shared with the PR-wide native path so both strategies screen
///     identically. Flag off ⇒ no-op.
/// </summary>
internal sealed class FileByFileSemanticScreeningStage(
    ISemanticCommentScreener screener,
    IProtocolRecorder? protocolRecorder = null) : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "file-by-file.semantic-screening";

    private readonly SemanticScreeningApplier _applier = new(screener, protocolRecorder);

    public string StageId => StageIdConstant;

    public async Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        var result = context.ReviewResult;
        if (result is null || result.Comments.Count == 0 || !context.FileReviewContext.EnableLanguageRobustScreening)
        {
            return context;
        }

        var updated = await this._applier
            .ApplyAsync(result, context.Job.ClientId, context.ProtocolId, cancellationToken)
            .ConfigureAwait(false);

        return ReferenceEquals(updated, result) ? context : context with { ReviewResult = updated };
    }
}
