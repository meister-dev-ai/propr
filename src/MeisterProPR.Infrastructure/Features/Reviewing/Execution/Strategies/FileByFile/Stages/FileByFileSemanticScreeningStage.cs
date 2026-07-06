// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Language-robust replacement for the English phrase-list hedge/vague filters. When the client enables
///     language-robust screening, each comment is classified by <see cref="ISemanticCommentScreener" /> (embedding
///     similarity, multilingual) and demoted — never deleted:
///     <list type="bullet">
///         <item>Firm (and the conservative degraded default) is kept as a posted comment.</item>
///         <item>
///             Every non-Firm classification — hedged or vague, at any severity, including hedged ERROR/WARNING —
///             is folded into the review summary (summary-only). Interim semantics: hedged findings are NOT passed
///             through to the evidence verifier, because verification only engages comments whose text yields
///             extractable claims and the claim extractor is English-shaped, so a hedged non-English finding would
///             otherwise be published unscreened.
///         </item>
///     </list>
///     Upgrade path (follow-up, not implemented): thread the screening class into verification so hedged findings
///     become must-confirm-or-summarize and confirmed ones are published rewritten as firm.
///     Every fold emits a <c>comment_screening_disposition</c> trace; a degraded classification keeps every comment
///     and records one <c>comment_screening_degraded</c> event. Flag off ⇒ no-op.
/// </summary>
internal sealed class FileByFileSemanticScreeningStage(
    ISemanticCommentScreener screener,
    IProtocolRecorder? protocolRecorder = null) : IReviewPipelineStage<PerFileReviewContext>
{
    // This id is part of persisted/profile-selected Reviewing protocol identity.
    public const string StageIdConstant = "file-by-file.semantic-screening";

    public string StageId => StageIdConstant;

    public async Task<PerFileReviewContext> ExecuteAsync(PerFileReviewContext context, CancellationToken cancellationToken)
    {
        var result = context.ReviewResult;
        if (result is null || result.Comments.Count == 0 || !context.FileReviewContext.EnableLanguageRobustScreening)
        {
            return context;
        }

        var clientId = context.Job.ClientId;

        var kept = new List<ReviewComment>(result.Comments.Count);
        var foldedIntoSummary = new List<(ReviewComment Comment, CommentScreeningResult Screening)>();

        foreach (var comment in result.Comments)
        {
            var screening = await screener.ClassifyAsync(comment.Message, clientId, cancellationToken).ConfigureAwait(false);
            if (screening.IsDegraded)
            {
                // Screening is unavailable: keep every comment unscreened (never drop on a screening error) and
                // record a single degraded event rather than silently disabling screening for the file.
                await this.RecordDegradedAsync(context.ProtocolId, context.ChangedFile.Path, cancellationToken).ConfigureAwait(false);
                return context;
            }

            if (KeepsComment(screening.Class))
            {
                kept.Add(comment);
            }
            else
            {
                foldedIntoSummary.Add((comment, screening));
            }
        }

        if (foldedIntoSummary.Count == 0)
        {
            return context;
        }

        foreach (var folded in foldedIntoSummary)
        {
            await this.RecordDispositionAsync(context.ProtocolId, folded.Comment, folded.Screening, cancellationToken).ConfigureAwait(false);
        }

        var updated = result with
        {
            Comments = kept.AsReadOnly(),
            Summary = AppendSummaryOnlySection(result.Summary, foldedIntoSummary),
        };
        return context with { ReviewResult = updated };
    }

    // Interim semantics: only a Firm classification is posted. Every non-Firm classification — hedged or vague, at
    // any severity, including hedged ERROR/WARNING — folds to summary-only (never deleted). Hedged E/W is not passed
    // to the evidence verifier: verification only engages comments whose text yields extractable claims and the claim
    // extractor is English-shaped, so a hedged non-English finding would otherwise be published unscreened. The
    // upgrade path (thread the screening class into verification; hedged ⇒ must-confirm-or-summarize) is a follow-up.
    private static bool KeepsComment(CommentScreeningClass screeningClass)
    {
        return screeningClass == CommentScreeningClass.Firm;
    }

    private static string AppendSummaryOnlySection(
        string summary,
        IReadOnlyList<(ReviewComment Comment, CommentScreeningResult Screening)> folded)
    {
        var builder = new StringBuilder(summary);
        if (summary.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.AppendLine("Screened to summary (low-confidence or non-actionable — not posted as review threads):");
        foreach (var folding in folded)
        {
            var comment = folding.Comment;
            var location = comment.LineNumber is { } line
                ? $"{comment.FilePath}:{line}"
                : comment.FilePath ?? "(file)";
            builder.AppendLine($"- {location}: {comment.Message}");
        }

        return builder.ToString().TrimEnd();
    }

    private async Task RecordDispositionAsync(Guid? protocolId, ReviewComment comment, CommentScreeningResult screening, CancellationToken ct)
    {
        if (protocolRecorder is null || !protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordReviewStrategyEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.CommentScreeningDisposition,
            JsonSerializer.Serialize(
                new
                {
                    filePath = comment.FilePath,
                    lineNumber = comment.LineNumber,
                    severity = comment.Severity.ToString(),
                    screeningClass = screening.Class.ToString(),
                    similarity = screening.Similarity,
                    disposition = "summary_only",
                }),
            null,
            null,
            ct).ConfigureAwait(false);
    }

    private async Task RecordDegradedAsync(Guid? protocolId, string filePath, CancellationToken ct)
    {
        if (protocolRecorder is null || !protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordReviewStrategyEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.CommentScreeningDegraded,
            JsonSerializer.Serialize(new { filePath }),
            null,
            null,
            ct).ConfigureAwait(false);
    }
}
