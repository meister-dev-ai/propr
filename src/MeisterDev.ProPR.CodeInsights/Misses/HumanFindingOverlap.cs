// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Services;

namespace MeisterDev.ProPR.CodeInsights.Misses;

/// <summary>One collected finding, reduced to what a duplicate check needs.</summary>
/// <param name="FilePath">File the finding is anchored to, or <c>null</c> for a pull-request-level finding.</param>
/// <param name="LineNumber">Line the finding is anchored to, or <c>null</c> when unknown.</param>
/// <param name="Message">The finding text.</param>
public sealed record FindingOverlapCandidate(string? FilePath, int? LineNumber, string Message);

/// <summary>
///     Decides whether a human review comment restates something ProPR already raised.
/// </summary>
/// <remarks>
///     <para>
///         This is the load-bearing rule of the whole recall measurement. Counting a human comment as a miss
///         when ProPR raised the same thing would penalise the reviewer for a finding it actually produced,
///         so the same issue must never end up counted as both a true positive and a false negative.
///     </para>
///     <para>
///         Two stages, cheap first. A human comment can only duplicate a finding anchored to the same file
///         within a small window of lines; comparing text across a whole pull request would match generic
///         phrasing ("this needs a null check") between unrelated places. Only within that window does it
///         compare wording, and it does so with the same token-set similarity the review pipeline's own
///         deduplication uses, rather than inventing a second notion of "the same finding".
///     </para>
/// </remarks>
public static class HumanFindingOverlap
{
    /// <summary>
    ///     How far apart two anchors may sit and still plausibly concern the same code. A human rarely
    ///     comments on the exact line a reviewer chose, so nearby counts; a different part of the file does not.
    /// </summary>
    public const int LineWindow = 10;

    /// <summary>
    ///     Token-overlap above which two messages are treated as the same concern.
    /// </summary>
    /// <remarks>
    ///     A judgement call, not a measured value: no labelled corpus of human comments paired with findings has
    ///     been graded against it yet. Two things fix it where it is. It sits below the 0.50 the review pipeline
    ///     collapses its own duplicates at, because there both texts were written by the same reviewer in the same
    ///     vocabulary, whereas here one side is a human comment and the shared token fraction is lower for the same
    ///     concern. And the two errors are not symmetric: treating distinct comments as duplicates loses one miss
    ///     from the count, while failing to spot a real duplicate invents a miss for a finding ProPR did raise,
    ///     which moves both recall and precision. So it errs loose. Calibrating it needs the labelling protocol,
    ///     and until that runs, treat the value as provisional.
    /// </remarks>
    public const double SimilarityThreshold = 0.32;

    /// <summary>
    ///     Returns whether <paramref name="humanComment" /> restates one of <paramref name="findings" />.
    /// </summary>
    /// <param name="filePath">File the human comment is anchored to, or <c>null</c> for a thread on the pull request.</param>
    /// <param name="lineNumber">Line the human comment is anchored to, or <c>null</c> when unknown.</param>
    /// <param name="humanComment">The human comment's text.</param>
    /// <param name="findings">The collected findings for the same pull request.</param>
    public static bool DuplicatesAnyFinding(
        string? filePath,
        int? lineNumber,
        string humanComment,
        IReadOnlyList<FindingOverlapCandidate> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        if (string.IsNullOrWhiteSpace(humanComment))
        {
            return false;
        }

        foreach (var finding in findings)
        {
            if (!AnchorsCouldConcernTheSameCode(filePath, lineNumber, finding))
            {
                continue;
            }

            if (FindingDeduplicator.JaccardSimilarity(humanComment, finding.Message) >= SimilarityThreshold)
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnchorsCouldConcernTheSameCode(
        string? filePath,
        int? lineNumber,
        FindingOverlapCandidate finding)
    {
        // Different files are never the same concern, and a file-anchored comment is never a duplicate of a
        // pull-request-level finding (or the reverse): a summary remark would otherwise swallow every inline
        // comment that happened to share its vocabulary.
        if (!string.Equals(filePath, finding.FilePath, StringComparison.Ordinal))
        {
            return false;
        }

        // Same file, and at least one side has no line to compare: the file alone has to carry it.
        if (lineNumber is null || finding.LineNumber is null)
        {
            return true;
        }

        return Math.Abs(lineNumber.Value - finding.LineNumber.Value) <= LineWindow;
    }
}
