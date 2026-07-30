// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

/// <summary>A human-authored review thread presented for judgement.</summary>
/// <param name="ClientId">The client whose model applies.</param>
/// <param name="ProviderThreadId">The thread's provider identity, for diagnostics.</param>
/// <param name="FilePath">File the thread is anchored to, when applicable.</param>
/// <param name="Discussion">The thread's discussion, as the crawl observed it.</param>
/// <param name="ThreadResolved">
///     Whether the provider reports the thread as resolved. Evidence for whether it was acted on, though not
///     proof: some teams resolve threads as housekeeping.
/// </param>
public sealed record HumanMissJudgementRequest(
    Guid ClientId,
    string ProviderThreadId,
    string? FilePath,
    string Discussion,
    bool ThreadResolved);

/// <summary>
///     The three separate judgements a human thread has to pass to count as something ProPR missed. They are
///     returned (and stored) separately rather than as one verdict, so a change to where the scope cut-off
///     sits can be re-applied to what was already harvested instead of re-judging every thread again.
/// </summary>
/// <param name="IsSubstantive">A real code issue, not a question, a nit, an approval, or chatter.</param>
/// <param name="WasActedOn">Accepted, or it led to a code change.</param>
/// <param name="IsInScope">
///     Within the class of issues an automated reviewer should reasonably catch: that is, not so
///     domain-specific, contextual, or exotic that missing it says nothing about review quality.
/// </param>
/// <param name="Confidence">The classifier's own confidence across the three, 0–1.</param>
/// <param name="Rationale">Short reason, kept for error analysis.</param>
public sealed record HumanMissJudgement(
    bool IsSubstantive,
    bool WasActedOn,
    bool IsInScope,
    double Confidence,
    string Rationale);

/// <summary>
///     Judges whether a human-authored review thread describes something ProPR should have found.
/// </summary>
public interface IHumanMissClassifier
{
    /// <summary>Identifier of this classifier's prompt and parsing behaviour, stamped onto each harvested miss.</summary>
    string ClassifierVersion { get; }

    /// <summary>
    ///     Judges one human thread, or returns <see langword="null" /> when it could not be judged. Never
    ///     throws except for cancellation.
    /// </summary>
    Task<HumanMissJudgement?> JudgeAsync(HumanMissJudgementRequest request, CancellationToken ct = default);
}
