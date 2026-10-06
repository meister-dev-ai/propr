// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

internal static class ReviewFindingPublicationMapper
{
    // Pair actual receipts by finding ordinal. An anchor fallback requires one candidate and one receipt;
    // ambiguous same-anchor findings retain unknown publication identity. Each receipt is consumed once.
    internal static IReadOnlyList<ReviewFindingProduced> BuildProducedFindings(
        IReadOnlyList<ReviewComment> comments, IReadOnlyList<ReviewComment> published,
        ReviewCommentPostingDiagnosticsDto diagnostics, bool postingEnabled, string providerScope)
    {
        var mapping = MapPublishedOrdinalsToPersisted(comments, published).ToList();
        var unclaimed = diagnostics.PostedComments.Where(row => row.ThreadKind == PostedReviewCommentKind.Inline).ToList();
        var produced = new List<ReviewFindingProduced>(comments.Count);
        for (var ordinal = 0; ordinal < comments.Count; ordinal++)
        {
            var comment = comments[ordinal];
            var publishedOrdinal = mapping.IndexOf(ordinal);
            var suppressed = diagnostics.SuppressedFindings.FirstOrDefault(row => row.Ordinal == publishedOrdinal);
            var suspect = diagnostics.PostedFindingNearMisses.FirstOrDefault(row => row.Ordinal == publishedOrdinal);
            var match = ClaimReceipt(unclaimed, published, comment, publishedOrdinal, suppressed);
            var failed = HasPostingFailure(diagnostics, published, comment, publishedOrdinal);
            var state = ResolvePublicationState(comment, match, postingEnabled, publishedOrdinal, suppressed, failed);
            produced.Add(
                new ReviewFindingProduced(
                    ordinal, comment.FilePath, comment.LineNumber, comment.Severity,
                    comment.Message, comment.OriginPassKind, comment.OriginPassIndex, comment.OriginPassLens, comment.OriginPassShadow,
                    comment.ScopeRelation, comment.SourceReadGrounding, match?.ProviderThreadId, match?.ProviderCommentId,
                    comment.OriginModelId, comment.OriginLogicalModelName, comment.OriginSymbolName, comment.OriginSymbolKind,
                    state, suppressed?.ReasonCode ?? state.ToString(), suppressed?.MatchedProviderThreadId,
                    suspect is null ? CodeInsightDuplicateState.Unknown : CodeInsightDuplicateState.Suspected,
                    suspect?.MatchedProviderThreadId, suspect is null ? null : "publication-near-match", suspect?.MatchScore, null, providerScope));
        }

        return produced;
    }

    private static bool HasPostingFailure(
        ReviewCommentPostingDiagnosticsDto diagnostics, IReadOnlyList<ReviewComment> published, ReviewComment comment, int publishedOrdinal)
    {
        return publishedOrdinal >= 0 && diagnostics.PostingFailures.Any(row => row.ThreadKind == "inline" &&
                                                                               (row.FindingOrdinal == publishedOrdinal ||
                                                                                row.FindingOrdinal is null &&
                                                                                published.Count(candidate =>
                                                                                    candidate.FilePath == comment.FilePath &&
                                                                                    candidate.LineNumber == comment.LineNumber) == 1 &&
                                                                                row.FilePath == comment.FilePath && row.Line == comment.LineNumber));
    }

    private static CodeInsightPublicationState ResolvePublicationState(
        ReviewComment comment, PostedReviewCommentRef? receipt, bool postingEnabled, int publishedOrdinal,
        ReviewCommentSuppressionRecord? suppression, bool failed)
    {
        if (receipt is not null)
        {
            return CodeInsightPublicationState.Published;
        }

        if (comment.OriginPassShadow)
        {
            return CodeInsightPublicationState.Shadow;
        }

        if (!postingEnabled)
        {
            return CodeInsightPublicationState.PostingDisabled;
        }

        if (publishedOrdinal < 0)
        {
            return CodeInsightPublicationState.PolicyWithheld;
        }

        if (suppression is not null)
        {
            return CodeInsightPublicationState.SuppressedRepeat;
        }

        return failed ? CodeInsightPublicationState.Failed : CodeInsightPublicationState.Unknown;
    }

    private static PostedReviewCommentRef? ClaimReceipt(
        List<PostedReviewCommentRef> unclaimed, IReadOnlyList<ReviewComment> published, ReviewComment comment,
        int publishedOrdinal, ReviewCommentSuppressionRecord? suppressed)
    {
        var matchIndex = publishedOrdinal < 0 ? -1 : unclaimed.FindIndex(row => row.FindingOrdinal == publishedOrdinal);
        if (matchIndex < 0 && publishedOrdinal >= 0 && suppressed is null
            && published.Count(row => row.FilePath == comment.FilePath && row.LineNumber == comment.LineNumber) == 1
            && unclaimed.Count(row => row.FindingOrdinal is null && row.FilePath == comment.FilePath && row.Line == comment.LineNumber) == 1)
        {
            matchIndex = unclaimed.FindIndex(row => row.FindingOrdinal is null && row.FilePath == comment.FilePath && row.Line == comment.LineNumber);
        }

        PostedReviewCommentRef? match = null;
        if (matchIndex >= 0)
        {
            match = unclaimed[matchIndex];
            unclaimed.RemoveAt(matchIndex);
        }

        return match;
    }

    internal static IReadOnlyList<int> MapPublishedOrdinalsToPersisted(
        IReadOnlyList<ReviewComment> persisted,
        IReadOnlyList<ReviewComment> published)
    {
        var map = new int[published.Count];
        var persistedIndex = 0;

        for (var publishedIndex = 0; publishedIndex < published.Count; publishedIndex++)
        {
            // The filter preserves order and keeps object identity, so advancing a single cursor pairs them.
            while (persistedIndex < persisted.Count
                   && !ReferenceEquals(persisted[persistedIndex], published[publishedIndex]))
            {
                persistedIndex++;
            }

            map[publishedIndex] = persistedIndex < persisted.Count ? persistedIndex : publishedIndex;
            persistedIndex++;
        }

        return map;
    }
}
