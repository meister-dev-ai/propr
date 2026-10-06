// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     Something a human reviewer raised that ProPR did not: a false negative. Without these, precision is
///     the only thing measurable, and precision alone flatters any reviewer that simply says less.
/// </summary>
/// <remarks>
///     Lives under the same pull-request aggregate as findings, so retention and the client cascade are
///     shared rather than reimplemented. The three judgements are stored separately from the verdict on
///     purpose: a threshold change can then be re-applied to what was already harvested instead of
///     re-judging every thread through the model again.
/// </remarks>
public sealed class CodeInsightMiss
{
    /// <summary>Canonical core-type membership captured by the classification workflow.</summary>
    public string TypeMembership { get; set; } = string.Empty;

    /// <summary>Provider/host scope recorded from the thread connection.</summary>
    public string ProviderScope { get; set; } = string.Empty;

    /// <summary>Finding qualifier captured from this human issue.</summary>
    public MeisterDev.ProPR.Domain.Enums.CodeInsightFindingQualifier? Qualifier { get; set; }

    /// <summary>Version and confidence of the miss dimension judgement.</summary>
    public string? DimensionClassifierVersion { get; set; }

    /// <summary>Confidence in the recorded dimensions.</summary>
    public double? DimensionConfidence { get; set; }

    /// <summary>Whether dimension enrichment failed independently of the human judgement.</summary>
    public bool DimensionJudgementFailed { get; set; }

    /// <summary>Bounded failed enrichment attempts for the retained source and dimension version.</summary>
    public int FailedDimensionAttempts { get; set; }

    /// <summary>Whether the latest judgement failed; such a row cannot supply a miss verdict.</summary>
    public bool JudgementFailed { get; set; }

    /// <summary>Failed judgement attempts, bounded by collection.</summary>
    public int FailedJudgementAttempts { get; set; }

    /// <summary>Hash of observed discussion, anchor and native status for bounded current-evidence refresh.</summary>
    public string? SourceFingerprint { get; set; }

    /// <summary>Newest retained source observation, independent of classifier completion time.</summary>
    public DateTimeOffset? SourceObservedAt { get; set; }

    /// <summary>Current source overlaps a retained reviewer finding or contains an AI-authored comment.</summary>
    public bool ExcludedAsOwnFinding { get; private set; }

    /// <summary>Changes overlap eligibility while preserving the human judgement and its evidence.</summary>
    public void RecordOwnFindingExclusion(bool excluded, DateTimeOffset updatedAt)
    {
        if (this.ExcludedAsOwnFinding == excluded)
        {
            return;
        }

        this.ExcludedAsOwnFinding = excluded;
        this.CountsAsMiss = !excluded && !this.JudgementFailed && this.IsSubstantive && this.WasActedOn && this.IsInScope;
        this.LastJudgedAt = updatedAt;
    }

    /// <summary>Unique identifier for this record.</summary>
    public Guid Id { get; init; }

    /// <summary>Owning aggregate: the pull request the human comment was made on.</summary>
    public Guid CodeInsightPullRequestId { get; init; }

    /// <summary>Navigation to the owning aggregate.</summary>
    public CodeInsightPullRequest? CodeInsightPullRequest { get; init; }

    /// <summary>Provider thread the human comment belongs to. The identity used to avoid harvesting it twice.</summary>
    public string ProviderThreadId { get; init; } = string.Empty;

    /// <summary>File the comment was anchored to, or <see langword="null" /> for a pull-request-level thread.</summary>
    public string? FilePath { get; init; }

    /// <summary>Line the comment was anchored to, or <see langword="null" /> when unknown.</summary>
    public int? LineNumber { get; init; }

    /// <summary>
    ///     The discussion the judgement was made from, encrypted at rest. Replaced along with the judgement when a
    ///     thread is judged again, so the stored evidence is always the text the stored verdict came from.
    /// </summary>
    public string EncryptedDiscussion { get; private set; } = string.Empty;

    /// <summary>Whether the thread was judged to describe a substantive code issue rather than a question or nit.</summary>
    public bool IsSubstantive { get; private set; }

    /// <summary>Whether the thread was judged to have been accepted or to have led to a code change.</summary>
    public bool WasActedOn { get; private set; }

    /// <summary>
    ///     Whether the issue was judged to be within the class ProPR should reasonably catch. The cut-off this
    ///     encodes is a calibration decision, which is why the judgement is stored rather than only its effect.
    /// </summary>
    public bool IsInScope { get; private set; }

    /// <summary>
    ///     Whether all three judgements held: that is, whether this counts toward recall. Stored on the row, not
    ///     computed on read, so a threshold change can be re-applied to what was already harvested without
    ///     re-judging it through the model. A thread that restated one of ProPR's own findings never reaches this
    ///     type, because the harvester drops it before asking for a judgement.
    /// </summary>
    public bool CountsAsMiss { get; private set; }

    /// <summary>The classifier's confidence in its judgements, 0–1.</summary>
    public double? ClassifierConfidence { get; private set; }

    /// <summary>Identifier of the classifier that judged this thread.</summary>
    public string ClassifierVersion { get; private set; } = string.Empty;

    /// <summary>UTC timestamp when the thread was first harvested.</summary>
    public DateTimeOffset HarvestedAt { get; init; }

    /// <summary>
    ///     Whether the thread was resolved at the provider when the stored judgement was made.
    /// </summary>
    /// <remarks>
    ///     <see cref="WasActedOn" /> asks whether the concern was accepted or led to a code change. A thread that
    ///     is still open has not been accepted and has led to nothing yet, so a judgement made against an open
    ///     thread answers that question with a no it could not have answered otherwise. Recording the state the
    ///     judgement was made against is what lets a provisional judgement be told apart from a settled one and
    ///     replaced when the thread resolves.
    /// </remarks>
    public bool JudgedThreadResolved { get; private set; }

    /// <summary>
    ///     UTC timestamp of the stored judgement or a current overlap eligibility change. Equal to
    ///     <see cref="HarvestedAt" /> until current evidence changes. Ordering-only observations leave it fixed.
    /// </summary>
    public DateTimeOffset LastJudgedAt { get; private set; }

    /// <summary>
    ///     Records a judgement over this thread, replacing any judgement already stored.
    /// </summary>
    /// <remarks>
    ///     The only way the judgement fields are written, on the first harvest and on every re-judgement alike.
    ///     <see cref="CountsAsMiss" /> is computed from the three judgements here and cannot be supplied, so a row
    ///     whose verdict contradicts its own components — a recall number nothing on the row accounts for — has no
    ///     way to be written. The discussion travels with them because a re-judgement reads a thread that has grown
    ///     since the first harvest, and the stored evidence has to be the text the stored verdict came from.
    /// </remarks>
    /// <param name="isSubstantive">Judged a real code issue, not a question or a nit.</param>
    /// <param name="wasActedOn">Judged accepted, or to have led to a code change.</param>
    /// <param name="isInScope">Judged within the class an automated reviewer should reasonably catch.</param>
    /// <param name="classifierConfidence">The classifier's confidence, 0–1.</param>
    /// <param name="classifierVersion">Identifier of the classifier that judged it.</param>
    /// <param name="judgedThreadResolved">Whether the thread was resolved at the provider when this was judged.</param>
    /// <param name="encryptedDiscussion">The discussion this judgement was made from, already encrypted.</param>
    /// <param name="judgedAt">UTC timestamp of the judgement.</param>
    public void RecordJudgement(
        bool isSubstantive,
        bool wasActedOn,
        bool isInScope,
        double? classifierConfidence,
        string classifierVersion,
        bool judgedThreadResolved,
        string encryptedDiscussion,
        DateTimeOffset judgedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(classifierVersion);
        ArgumentNullException.ThrowIfNull(encryptedDiscussion);

        this.IsSubstantive = isSubstantive;
        this.WasActedOn = wasActedOn;
        this.IsInScope = isInScope;
        this.ClassifierConfidence = classifierConfidence;
        this.ClassifierVersion = classifierVersion;
        this.JudgedThreadResolved = judgedThreadResolved;
        this.EncryptedDiscussion = encryptedDiscussion;
        this.LastJudgedAt = judgedAt;

        // Computed here and taken from nobody, so a stored verdict cannot contradict the judgements beside it.
        // A caller passing the verdict in was free to disagree with its own three answers, and the recall count
        // reads this column, so such a row would move a metric that nothing else on the row accounts for.
        this.CountsAsMiss = !this.ExcludedAsOwnFinding && !this.JudgementFailed && isSubstantive && wasActedOn && isInScope;
    }
}
