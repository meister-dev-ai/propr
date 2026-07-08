// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Screening;

/// <summary>
///     Shared language-robust screening applier. Classifies each comment in a review result via
///     <see cref="ISemanticCommentScreener" /> (embedding similarity, multilingual) and demotes — never deletes —
///     every non-Firm comment into the review summary. Degraded-safe: on the first degraded classification it keeps
///     every comment and records a single <c>comment_screening_degraded</c> event; each fold records a
///     <c>comment_screening_disposition</c> event. This is the single implementation of the screening semantics
///     shared by the file-by-file screening stage and the PR-wide native path, so both strategies screen identically.
///     Callers gate the invocation on the client's language-robust-screening flag.
/// </summary>
internal sealed class SemanticScreeningApplier(
    ISemanticCommentScreener screener,
    IProtocolRecorder? protocolRecorder = null)
{
    /// <summary>
    ///     Returns <paramref name="result" /> with every non-Firm comment folded into the summary. The original
    ///     result is returned unchanged when there are no comments, when nothing folds, or when screening degrades.
    /// </summary>
    public async Task<ReviewResult> ApplyAsync(ReviewResult result, Guid clientId, Guid? protocolId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Comments.Count == 0)
        {
            return result;
        }

        var kept = new List<ReviewComment>(result.Comments.Count);
        var foldedIntoSummary = new List<(ReviewComment Comment, CommentScreeningResult Screening)>();

        foreach (var comment in result.Comments)
        {
            var screening = await screener.ClassifyAsync(comment.Message, clientId, ct).ConfigureAwait(false);
            if (screening.IsDegraded)
            {
                // Screening is unavailable: keep every comment unscreened (never drop on a screening error) and
                // record a single degraded event rather than silently disabling screening.
                await this.RecordDegradedAsync(protocolId, ct).ConfigureAwait(false);
                return result;
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
            return result;
        }

        foreach (var folded in foldedIntoSummary)
        {
            await this.RecordDispositionAsync(protocolId, folded.Comment, folded.Screening, ct).ConfigureAwait(false);
        }

        return result with
        {
            Comments = kept.AsReadOnly(),
            Summary = AppendSummaryOnlySection(result.Summary, foldedIntoSummary),
        };
    }

    // Interim semantics: only a Firm classification is posted. Every non-Firm classification — hedged or vague, at
    // any severity, including hedged ERROR/WARNING — folds to summary-only (never deleted). Hedged E/W is not passed
    // to the evidence verifier: verification only engages comments whose text yields extractable claims and the claim
    // extractor is English-shaped, so a hedged non-English finding would otherwise be published unscreened.
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

    private async Task RecordDegradedAsync(Guid? protocolId, CancellationToken ct)
    {
        if (protocolRecorder is null || !protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordReviewStrategyEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.CommentScreeningDegraded,
            JsonSerializer.Serialize(new { reason = "screening_unavailable" }),
            null,
            null,
            ct).ConfigureAwait(false);
    }
}
