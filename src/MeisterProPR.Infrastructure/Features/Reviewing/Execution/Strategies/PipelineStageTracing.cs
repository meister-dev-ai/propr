// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;

/// <summary>
///     Emits per-comment disposition traces for the surviving deterministic per-file pipeline stages so their
///     effect on the posted comment set is observable in the protocol, mirroring the semantic screening stage.
///     Every method is a no-op when no recorder or protocol id is available.
/// </summary>
internal static class PipelineStageTracing
{
    // The info-strip stage removes exactly the INFO-severity comments, so the dropped set is those entries
    // of the pre-strip result; one trace is emitted per stripped comment.
    public static async Task RecordInfoStrippedAsync(
        IProtocolRecorder? protocolRecorder,
        Guid? protocolId,
        ReviewResult? before,
        ReviewResult? after,
        CancellationToken ct)
    {
        if (protocolRecorder is null || !protocolId.HasValue || before is null || after is null ||
            after.Comments.Count == before.Comments.Count)
        {
            return;
        }

        foreach (var comment in before.Comments.Where(comment => comment.Severity == CommentSeverity.Info))
        {
            await protocolRecorder.RecordReviewStrategyEventAsync(
                protocolId.Value,
                ReviewProtocolEventNames.CommentInfoStripped,
                JsonSerializer.Serialize(new { filePath = comment.FilePath, lineNumber = comment.LineNumber }),
                null,
                null,
                ct).ConfigureAwait(false);
        }
    }

    // The confidence-floor stage preserves comment order and count and only lowers severities, so a positional
    // comparison identifies each downgraded comment; one trace is emitted per downgrade.
    public static async Task RecordConfidenceDowngradesAsync(
        IProtocolRecorder? protocolRecorder,
        Guid? protocolId,
        ReviewResult? before,
        ReviewResult? after,
        int? finalConfidence,
        CancellationToken ct)
    {
        if (protocolRecorder is null || !protocolId.HasValue || before is null || after is null ||
            before.Comments.Count != after.Comments.Count)
        {
            return;
        }

        for (var index = 0; index < before.Comments.Count; index++)
        {
            var original = before.Comments[index];
            var adjusted = after.Comments[index];
            if (original.Severity == adjusted.Severity)
            {
                continue;
            }

            await protocolRecorder.RecordReviewStrategyEventAsync(
                protocolId.Value,
                ReviewProtocolEventNames.CommentSeverityDowngraded,
                JsonSerializer.Serialize(
                    new
                    {
                        filePath = original.FilePath,
                        lineNumber = original.LineNumber,
                        fromSeverity = original.Severity.ToString(),
                        toSeverity = adjusted.Severity.ToString(),
                        finalConfidence,
                    }),
                null,
                null,
                ct).ConfigureAwait(false);
        }
    }
}
