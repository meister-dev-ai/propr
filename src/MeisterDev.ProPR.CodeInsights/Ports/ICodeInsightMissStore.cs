// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;

namespace MeisterDev.ProPR.CodeInsights.Ports;

/// <summary>
///     Persistence boundary for the human review threads the reviewer never raised, and that makes recall
///     measurable rather than assumed.
/// </summary>
/// <remarks>
///     These records are evidence about the reviewer rather than output from it, so they are kept apart from the
///     findings it produced. A harvester needs both boundaries and says so by depending on both.
/// </remarks>
public sealed record CodeInsightMissObservation(
    string? SourceFingerprint,
    bool JudgementFailed,
    int FailedAttempts,
    bool IsSubstantive = false,
    bool WasActedOn = false,
    bool IsInScope = false,
    double? Confidence = null,
    bool JudgedThreadResolved = false,
    string TypeMembership = "",
    MeisterDev.ProPR.Domain.Enums.CodeInsightFindingQualifier? Qualifier = null,
    string? DimensionClassifierVersion = null,
    double? DimensionConfidence = null,
    bool DimensionJudgementFailed = false,
    int FailedDimensionAttempts = 0,
    DateTimeOffset? SourceObservedAt = null,
    bool ExcludedAsOwnFinding = false);

/// <summary>Whether an observation is already retained and whether score inputs changed.</summary>
public sealed record CodeInsightMissAcknowledgment(bool Retained, bool Changed);

/// <summary>Latest structural eligibility; an eligible observation does not establish a completed judgement.</summary>
public sealed record CodeInsightThreadEligibilityObservation(DateTimeOffset SourceObservedAt, bool ExcludedFromHumanMisses);

public interface ICodeInsightMissStore
{
    /// <summary>Retains the ordered source eligibility without creating a harvested human thread.</summary>
    Task<CodeInsightThreadEligibilityObservation> ObserveThreadEligibilityAsync(
        CodeInsightPullRequestKey key, string threadId, string providerScope, bool excludedFromHumanMisses,
        DateTimeOffset observedAt, CancellationToken ct = default);

    /// <summary>Returns structural eligibility separately from retained classifier judgements.</summary>
    Task<CodeInsightThreadEligibilityObservation?> GetThreadEligibilityAsync(
        CodeInsightPullRequestKey key, string threadId, string providerScope, CancellationToken ct = default);

    /// <summary>Atomically acknowledges completed or superseded source evidence and advances its ordering watermark.</summary>
    Task<CodeInsightMissAcknowledgment> ObserveUnchangedMissAsync(
        CodeInsightPullRequestKey key, string threadId, string fingerprint,
        DateTimeOffset observedAt, CancellationToken ct = default, Guid? connectionId = null,
        string? dimensionClassifierVersion = null, bool excludedAsOwnFinding = false, string? providerScope = null) =>
        Task.FromResult(new CodeInsightMissAcknowledgment(false, false));

    /// <summary>Current source identity for idempotent observations and bounded classifier retries.</summary>
    Task<CodeInsightMissObservation?> GetObservationAsync(
        CodeInsightPullRequestKey key, string threadId, CancellationToken ct = default, Guid? connectionId = null, string? providerScope = null) =>
        Task.FromResult<CodeInsightMissObservation?>(null);

    /// <summary>
    ///     Records a source-qualified human thread and returns whether score inputs changed. Timestamped
    ///     observations update an existing row only when current. Legacy calls without a source timestamp
    ///     retain first-insert semantics. Replays keep one row and one contribution to recall.
    /// </summary>
    Task<bool> RecordMissAsync(
        CodeInsightPullRequestKey key,
        CodeInsightMissRecord miss,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns whether the stored judgement for this human thread was made against a resolved thread, or
    ///     <see langword="null" /> when the thread has not been harvested for this pull request at all.
    /// </summary>
    /// <remarks>
    ///     The three-way answer is what the harvester needs. Not harvested means judge it; harvested while the
    ///     thread was open means the judgement is provisional and must be replaced once the thread resolves;
    ///     harvested while resolved means it is settled and costs no further model call.
    /// </remarks>
    Task<bool?> GetJudgedThreadResolvedAsync(
        CodeInsightPullRequestKey key,
        string providerThreadId,
        CancellationToken ct = default,
        Guid? connectionId = null, string? providerScope = null);

    /// <summary>
    ///     Replaces current judgement and discussion when source ordering permits, returning whether score
    ///     inputs changed. A failed same-source replay preserves successful evidence. Identity, anchor and
    ///     first harvest time remain fixed.
    /// </summary>
    Task<bool> RejudgeMissAsync(
        CodeInsightPullRequestKey key,
        CodeInsightMissRecord miss,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the harvested misses for one pull request (with decrypted discussion), so they are
    ///     inspectable before any dashboard exists. Ordered oldest first.
    /// </summary>
    Task<IReadOnlyList<CodeInsightMissView>> GetMissesForPullRequestAsync(
        CodeInsightPullRequestKey key,
        CancellationToken ct = default);
}
