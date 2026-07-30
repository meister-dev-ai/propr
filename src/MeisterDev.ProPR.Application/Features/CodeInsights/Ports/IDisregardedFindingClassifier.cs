// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

/// <summary>One disregarded finding and the discussion that closed it, presented for judgement.</summary>
/// <param name="ClientId">The client whose model applies.</param>
/// <param name="FindingId">The finding's surrogate identity.</param>
/// <param name="FindingMessage">What the reviewer said.</param>
/// <param name="FilePath">File the finding was anchored to, when applicable.</param>
/// <param name="CommentHistory">The thread's discussion, as the crawl observed it.</param>
/// <param name="ChangeExcerpt">The relevant diff excerpt, when the crawl had one.</param>
public sealed record DisregardedFindingJudgementRequest(
    Guid ClientId,
    Guid FindingId,
    string FindingMessage,
    string? FilePath,
    string CommentHistory,
    string? ChangeExcerpt);

/// <summary>Whether a disregarded finding was wrong or correct but unwanted here, and why.</summary>
/// <param name="WasWrong">
///     True when the reviewer was mistaken: a false positive. False when the finding was correct and the
///     team simply did not want it acted on.
/// </param>
/// <param name="Confidence">The classifier's own confidence, 0–1.</param>
/// <param name="Rationale">Short reason, kept for error analysis rather than for display.</param>
/// <param name="Reason">
///     Which of the five rejection reasons applies, or <see langword="null" /> when the classifier could
///     judge the split but not the reason behind it. Kept separate from <paramref name="WasWrong" /> so an
///     unjudged reason costs the reason only, rather than discarding a usable outcome with it.
/// </param>
/// <param name="IsUnresolved">
///     True when a human engaged with the finding and no verdict was reached, which is neither an acceptance
///     nor a rejection. <paramref name="WasWrong" /> and <paramref name="Reason" /> carry nothing in that case:
///     nobody said the finding was wrong, and nobody said why it was turned down, because it was not.
/// </param>
public sealed record DisregardedFindingJudgement(
    bool WasWrong,
    double Confidence,
    string Rationale,
    CodeInsightRejectionReason? Reason = null,
    bool IsUnresolved = false);

/// <summary>
///     Judges whether a disregarded finding was wrong or merely unwanted, and why it was turned down. An SCM
///     thread status cannot tell these apart (both look like "closed") and conflating them would put every
///     unwanted-but-correct finding into the false-positive count and make the reviewer look worse than it is.
/// </summary>
/// <remarks>
///     The reason comes from the same call as the split rather than from a second one. Both answers are read
///     off the same discussion, so asking twice would double the cost of every rejection to learn nothing the
///     first answer did not already contain.
/// </remarks>
public interface IDisregardedFindingClassifier
{
    /// <summary>Identifier of this classifier's prompt and parsing behaviour, stamped onto each disposition.</summary>
    string ClassifierVersion { get; }

    /// <summary>
    ///     Judges one disregarded finding, or returns <see langword="null" /> when it could not be judged.
    ///     Never throws except for cancellation.
    /// </summary>
    Task<DisregardedFindingJudgement?> JudgeAsync(
        DisregardedFindingJudgementRequest request,
        CancellationToken ct = default);
}
