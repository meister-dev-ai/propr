// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>
///     Identity of the pull request a set of collected code-insight facts belongs to. Provider-neutral and
///     carried by value; the store resolves or creates the aggregate from it.
/// </summary>
/// <param name="ClientId">Owning client.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Provider pull-request identifier.</param>
public sealed record CodeInsightPullRequestKey(
    Guid ClientId,
    string RepositoryId,
    long PullRequestId);

/// <summary>
///     One finding to materialise. The <paramref name="Ordinal" /> is the finding's position within its
///     increment and, together with the increment's revision key, forms the natural key that makes
///     re-materialisation idempotent: identity never depends on the message text.
/// </summary>
/// <param name="Ordinal">Zero-based position of the finding within the increment's finding list.</param>
/// <param name="FilePath">File the finding is anchored to, or <c>null</c> for pull-request-level findings.</param>
/// <param name="LineNumber">Line the finding is anchored to, or <c>null</c> when unknown.</param>
/// <param name="Severity">Severity the review assigned.</param>
/// <param name="Message">The finding text; the store encrypts it at rest.</param>
/// <param name="OriginPassKind">Review pass that produced the finding, when known.</param>
/// <param name="OriginPassIndex">1-based index of the numbered multi-pass union pass, when applicable.</param>
/// <param name="OriginPassLens">Specialist lens the producing pass ran under, when applicable.</param>
/// <param name="OriginPassShadow">Whether the finding came from a shadow pass.</param>
/// <param name="ScopeRelation">The anchor's position relative to the increment's changed ranges.</param>
/// <param name="SourceReadGrounding">Whether the reviewer read the cited line while producing the finding.</param>
/// <param name="ProviderThreadId">Provider thread the finding was posted as, when it was posted.</param>
/// <param name="ProviderCommentId">Provider comment the finding was posted as, when it was posted.</param>
/// <param name="OriginModelId">The remote model that produced the finding, when a single pass owns it.</param>
/// <param name="OriginLogicalModelName">
///     The client's logical model name for the producing pass, when it ran through a named logical model.
/// </param>
/// <param name="OriginSymbolName">Definition the finding's line falls inside, resolved structurally.</param>
/// <param name="OriginSymbolKind">What kind of definition that is.</param>
public sealed record CodeInsightFindingSnapshot(
    int Ordinal,
    string? FilePath,
    int? LineNumber,
    CommentSeverity Severity,
    string Message,
    string? OriginPassKind,
    int? OriginPassIndex,
    string? OriginPassLens,
    bool OriginPassShadow,
    ReviewCommentScopeRelation? ScopeRelation,
    ReviewCommentReadGrounding? SourceReadGrounding,
    string? ProviderThreadId,
    string? ProviderCommentId,
    string? OriginModelId = null,
    string? OriginLogicalModelName = null,
    string? OriginSymbolName = null,
    string? OriginSymbolKind = null);

/// <summary>
///     A collected finding still awaiting type classification, with everything the classifier needs and
///     nothing more. The message is decrypted.
/// </summary>
/// <param name="Id">Surrogate identity of the finding.</param>
/// <param name="ClientId">Owning client: decides the model, the vocabulary, and the gate.</param>
/// <param name="JobId">
///     The review job the finding came from. Carried so a sweep can refresh each affected job's roll-up once
///     rather than once per finding.
/// </param>
/// <param name="Message">The decrypted finding text.</param>
/// <param name="FilePath">File the finding is anchored to, when applicable.</param>
/// <param name="LineNumber">Line the finding is anchored to, when known.</param>
/// <param name="Severity">Severity the review assigned.</param>
/// <param name="OriginPassKind">Review pass that produced the finding, when known.</param>
/// <param name="Attempts">How many classification attempts have already been made.</param>
public sealed record CodeInsightUnclassifiedFinding(
    Guid Id,
    Guid ClientId,
    Guid JobId,
    string Message,
    string? FilePath,
    int? LineNumber,
    CommentSeverity Severity,
    string? OriginPassKind,
    int Attempts);

/// <summary>
///     The classification to record against one finding: its types, its level, its qualifier, and how sure
///     the classifier was.
/// </summary>
/// <param name="CoreSlugs">Core type slugs that apply.</param>
/// <param name="CustomTagIds">The client's custom tag identities that apply.</param>
/// <param name="Level">The level of code the finding concerns.</param>
/// <param name="Qualifier">Whether the code in question is absent, wrong, or unnecessary.</param>
/// <param name="Confidence">The classifier's confidence, 0–1.</param>
/// <param name="ClassifierVersion">Identifier of the classifier that produced this.</param>
public sealed record CodeInsightClassification(
    IReadOnlyList<string> CoreSlugs,
    IReadOnlyList<Guid> CustomTagIds,
    CodeInsightFindingLevel Level,
    CodeInsightFindingQualifier Qualifier,
    double Confidence,
    string ClassifierVersion);

/// <summary>
///     What became of one finding, with the signals it was derived from. The signals are carried rather than
///     discarded so a disagreement about an outcome can be settled from what it was derived from instead of
///     by re-judging a thread that has since moved on.
/// </summary>
/// <param name="Disposition">What became of the finding.</param>
/// <param name="SourceIntent">The provider-neutral meaning of the thread's close.</param>
/// <param name="SourceCodeChange">Whether the anchored code changed since the finding was raised.</param>
/// <param name="ClassifierVersion">
///     Identifier of the classifier that produced the rejection split, or <c>null</c> when the disposition
///     followed from the signals alone and no classifier ran.
/// </param>
/// <param name="ClassifierConfidence">The classifier's confidence, or <c>null</c> when none ran.</param>
/// <param name="RejectionReason">
///     Why a rejected finding was rejected, or <c>null</c> when it was not rejected, when the reason could not
///     be judged, or when the disposition predates reasons being recorded.
/// </param>
public sealed record CodeInsightDispositionRecord(
    CodeInsightDisposition Disposition,
    ThreadResolutionIntent SourceIntent,
    ThreadAnchorCodeChange SourceCodeChange,
    string? ClassifierVersion,
    double? ClassifierConfidence,
    CodeInsightRejectionReason? RejectionReason = null);

/// <summary>
///     A human thread to record as something ProPR missed. The three judgements are carried separately from
///     the verdict so a change to where the scope cut-off sits can be re-applied without re-judging.
/// </summary>
/// <param name="ProviderThreadId">The human thread's provider identity, and its harvest identity.</param>
/// <param name="FilePath">File the thread is anchored to, when applicable.</param>
/// <param name="LineNumber">Line the thread is anchored to, when known.</param>
/// <param name="Discussion">The discussion the judgement was made from; the store encrypts it at rest.</param>
/// <param name="IsSubstantive">Judged a real code issue rather than a question or a nit.</param>
/// <param name="WasActedOn">Judged accepted, or to have led to a code change.</param>
/// <param name="IsInScope">Judged within the class an automated reviewer should reasonably catch.</param>
/// <param name="CountsAsMiss">Whether all three held and it did not restate a finding ProPR raised.</param>
/// <param name="Confidence">The classifier's confidence, 0–1.</param>
/// <param name="ClassifierVersion">Identifier of the classifier that judged it.</param>
public sealed record CodeInsightMissRecord(
    string ProviderThreadId,
    string? FilePath,
    int? LineNumber,
    string Discussion,
    bool IsSubstantive,
    bool WasActedOn,
    bool IsInScope,
    bool CountsAsMiss,
    double? Confidence,
    string ClassifierVersion);

/// <summary>A harvested miss as the store returns it, with the discussion decrypted.</summary>
/// <param name="Id">Surrogate identity of the harvested record.</param>
/// <param name="ProviderThreadId">The human thread's provider identity.</param>
/// <param name="FilePath">File the thread is anchored to, when applicable.</param>
/// <param name="LineNumber">Line the thread is anchored to, when known.</param>
/// <param name="Discussion">The decrypted discussion.</param>
/// <param name="IsSubstantive">Judged a real code issue.</param>
/// <param name="WasActedOn">Judged accepted or acted on.</param>
/// <param name="IsInScope">Judged in scope for an automated reviewer.</param>
/// <param name="CountsAsMiss">Whether it counts toward recall.</param>
/// <param name="Confidence">The classifier's confidence.</param>
/// <param name="ClassifierVersion">Identifier of the classifier that judged it.</param>
/// <param name="HarvestedAt">When it was harvested.</param>
public sealed record CodeInsightMissView(
    Guid Id,
    string ProviderThreadId,
    string? FilePath,
    int? LineNumber,
    string Discussion,
    bool IsSubstantive,
    bool WasActedOn,
    bool IsInScope,
    bool CountsAsMiss,
    double? Confidence,
    string ClassifierVersion,
    DateTimeOffset HarvestedAt);

/// <summary>
///     Where one finding stands in the classification pipeline. The three states are distinguished because a
///     freshly finished review legitimately shows no tags for a cycle or two, and "not yet" must not read the
///     same as "nothing to say".
/// </summary>
public enum CodeInsightClassificationStatus
{
    /// <summary>Classified: the tags, level, and qualifier are present.</summary>
    Classified = 0,

    /// <summary>Collected but not yet classified, and still within its retry allowance.</summary>
    Pending = 1,

    /// <summary>
    ///     Classification was attempted up to its ceiling without a usable result. Reported distinctly so a
    ///     finding the model cannot place is visible as such rather than as merely slow.
    /// </summary>
    Unclassifiable = 2,
}

/// <summary>
///     The classification of one finding of a review job, keyed by its position in that job's finding list.
/// </summary>
/// <remarks>
///     <paramref name="Ordinal" /> is the index of the finding in the job's persisted review result, which is
///     what lets a caller line these up with the findings it already renders without needing a finding id of
///     its own. Tag slugs are returned rather than identities: this is a display contract.
/// </remarks>
/// <param name="Ordinal">Index of the finding within its job's review result.</param>
/// <param name="Status">Where the finding stands in the classification pipeline.</param>
/// <param name="CoreTags">Core type slugs, comparable across clients. Empty unless classified.</param>
/// <param name="CustomTags">The client's own type slugs, including any since retired. Empty unless classified.</param>
/// <param name="Level">The level of code the finding concerns, when classified.</param>
/// <param name="Qualifier">Whether the code in question is absent, wrong, or unnecessary, when classified.</param>
/// <param name="Confidence">The classifier's confidence, when classified.</param>
public sealed record CodeInsightFindingClassificationView(
    int Ordinal,
    CodeInsightClassificationStatus Status,
    IReadOnlyList<string> CoreTags,
    IReadOnlyList<string> CustomTags,
    CodeInsightFindingLevel? Level,
    CodeInsightFindingQualifier? Qualifier,
    double? Confidence);

/// <summary>
///     A materialised finding as the store returns it. The message is decrypted; <paramref name="Id" /> is
///     the surrogate every downstream consumer keys on.
/// </summary>
/// <param name="Id">Surrogate identity assigned at materialisation.</param>
/// <param name="JobId">The review job that produced the finding.</param>
/// <param name="RevisionKey">Stored revision key of the producing increment.</param>
/// <param name="Ordinal">Position of the finding within its increment.</param>
/// <param name="FilePath">File the finding is anchored to, when applicable.</param>
/// <param name="LineNumber">Line the finding is anchored to, when known.</param>
/// <param name="Severity">Severity the review assigned.</param>
/// <param name="Message">The decrypted finding text.</param>
/// <param name="ProviderThreadId">Provider thread the finding was posted as, when it was posted.</param>
/// <param name="ObservedAt">When the finding was observed by the review that produced it.</param>
public sealed record CodeInsightFindingView(
    Guid Id,
    Guid JobId,
    string RevisionKey,
    int Ordinal,
    string? FilePath,
    int? LineNumber,
    CommentSeverity Severity,
    string Message,
    string? ProviderThreadId,
    DateTimeOffset ObservedAt);
