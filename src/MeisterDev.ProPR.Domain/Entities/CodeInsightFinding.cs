// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     A single ProPR finding materialised as a durable record. <see cref="Id" /> is a surrogate assigned
///     once at materialisation and is the only identity downstream consumers (type tags, dispositions,
///     memory links, and roll-ups) are allowed to key on. Nothing downstream may identify a finding by
///     comparing or scanning <see cref="EncryptedMessage" />.
/// </summary>
/// <remarks>
///     Idempotency rests on the natural key
///     (<see cref="CodeInsightPullRequestId" />, <see cref="RevisionKey" />, <see cref="FilePath" />,
///     <see cref="LineNumber" />, <see cref="Ordinal" />), never on the message text. The ordinal is part
///     of it because two findings may legitimately share a file and line within one increment.
/// </remarks>
public sealed class CodeInsightFinding
{
    /// <summary>
    ///     Surrogate identity assigned at materialisation. Stable for the record's lifetime and the join
    ///     key for every downstream consumer.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>Owning aggregate: the pull request this finding was raised on.</summary>
    public Guid CodeInsightPullRequestId { get; init; }

    /// <summary>Navigation to the owning aggregate.</summary>
    public CodeInsightPullRequest? CodeInsightPullRequest { get; init; }

    /// <summary>The review job that produced this finding.</summary>
    public Guid JobId { get; init; }

    /// <summary>Stored revision key of the review increment that produced this finding.</summary>
    public string RevisionKey { get; init; } = string.Empty;

    /// <summary>Zero-based position of the finding within its increment's finding list.</summary>
    public int Ordinal { get; init; }

    /// <summary>File the finding is anchored to, or <see langword="null" /> for pull-request-level findings.</summary>
    public string? FilePath { get; init; }

    /// <summary>Line the finding is anchored to, or <see langword="null" /> when unknown.</summary>
    public int? LineNumber { get; init; }

    /// <summary>Severity the review assigned to the finding.</summary>
    public CommentSeverity Severity { get; init; }

    /// <summary>
    ///     The finding text, encrypted at rest. Present for provenance and for later enrichment prompts;
    ///     it is deliberately never an identity or lookup key.
    /// </summary>
    public string EncryptedMessage { get; init; } = string.Empty;

    /// <summary>Review pass that produced the finding, when known.</summary>
    public string? OriginPassKind { get; init; }

    /// <summary>1-based index of the numbered multi-pass union pass that produced the finding, when applicable.</summary>
    public int? OriginPassIndex { get; init; }

    /// <summary>Specialist lens the producing pass ran under, when applicable.</summary>
    public string? OriginPassLens { get; init; }

    /// <summary>Whether the finding came from a shadow pass and was therefore never publishable.</summary>
    public bool OriginPassShadow { get; init; }

    /// <summary>
    ///     The remote model that produced the finding, or <see langword="null" /> when it was not recorded,
    ///     findings collected before models were attributed, and findings no single pass owns.
    /// </summary>
    /// <remarks>
    ///     Never inferred after the fact. A finding whose model was not recorded groups as unattributed, because a
    ///     guessed attribution would be indistinguishable from a measured one in every number derived from it.
    /// </remarks>
    public string? OriginModelId { get; init; }

    /// <summary>
    ///     The client's logical model name for the producing pass, when it ran through a named logical model rather
    ///     than a bare connection binding. Stored alongside <see cref="OriginModelId" /> rather than instead of it:
    ///     the name is what an operator configures and compares, and it can be repointed at a different remote
    ///     model, which is exactly the change a per-model reading has to be able to see.
    /// </summary>
    public string? OriginLogicalModelName { get; init; }

    /// <summary>
    ///     Name of the definition this finding's line falls inside (the method, class, or function it is about)
    ///     resolved structurally at review time, or <see langword="null" /> when nothing could place it.
    /// </summary>
    /// <remarks>
    ///     Name-based and per file: two overloads share a name, and the name carries no namespace. It is what makes
    ///     "which parts of this codebase keep producing findings" answerable, and it is the same key the reference
    ///     lookup already uses, which is what a dependency-weighted reading would join on.
    /// </remarks>
    public string? OriginSymbolName { get; init; }

    /// <summary>What kind of definition <see cref="OriginSymbolName" /> is. <see langword="null" /> whenever it is.</summary>
    public string? OriginSymbolKind { get; init; }

    /// <summary>The finding's anchor position relative to the increment's changed ranges, when classifiable.</summary>
    public ReviewCommentScopeRelation? ScopeRelation { get; init; }

    /// <summary>Whether the reviewer read the cited line while producing the finding, when applicable.</summary>
    public ReviewCommentReadGrounding? SourceReadGrounding { get; init; }

    /// <summary>
    ///     Provider thread the finding was posted as, when it was posted. This is the join key a
    ///     disposition consumer uses to recognise the finding when its thread later resolves; a finding
    ///     that was never published has none. Settable because a later posting pass can publish a finding
    ///     that was first collected unpublished: it is the only part of the record a re-materialisation
    ///     may legitimately fill in.
    /// </summary>
    public string? ProviderThreadId { get; set; }

    /// <summary>
    ///     Provider comment the finding was posted as, when it was posted. Settable for the same reason as
    ///     <see cref="ProviderThreadId" />.
    /// </summary>
    public string? ProviderCommentId { get; set; }

    /// <summary>
    ///     Identity of the <em>problem</em> rather than of this row. An increment that re-reports the same
    ///     problem gets a new row carrying the same chain, so "was it still being raised when the pull request
    ///     finished" becomes answerable: a chain whose newest row is the pull request's newest revision persisted,
    ///     and one whose newest row is older stopped being reported.
    /// </summary>
    /// <remarks>
    ///     A fresh problem starts its own chain. Matching is textual and same-file, so it is an estimate like
    ///     everything else derived from finding text, but an estimate that only ever mislabels a finding as new,
    ///     never as belonging to a chain it has nothing to do with.
    /// </remarks>
    public Guid FindingChainId { get; set; }

    /// <summary>UTC timestamp at which the finding was observed by the review that produced it.</summary>
    public DateTimeOffset ObservedAt { get; init; }

    /// <summary>UTC timestamp when this record was materialised.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    ///     Type tags assigned to this finding. A finding may carry several, and each is either a core type
    ///     (comparable across clients) or one of the owning client's custom tags.
    /// </summary>
    public ICollection<CodeInsightFindingTag> Tags { get; init; } = new List<CodeInsightFindingTag>();

    /// <summary>
    ///     The level of code the finding concerns, once classified; <see langword="null" /> while it is still
    ///     awaiting classification.
    /// </summary>
    public CodeInsightFindingLevel? Level { get; set; }

    /// <summary>
    ///     Whether the code in question is absent, wrong, or unnecessary, once classified;
    ///     <see langword="null" /> while it is still awaiting classification.
    /// </summary>
    public CodeInsightFindingQualifier? Qualifier { get; set; }

    /// <summary>
    ///     When the finding was successfully classified, or <see langword="null" /> while it is still
    ///     unclassified. This is the column the classification backlog query filters on.
    /// </summary>
    public DateTimeOffset? ClassifiedAt { get; set; }

    /// <summary>
    ///     How many classification attempts have been made. A finding is retried while this is below the
    ///     bounded ceiling, so a transient model failure does not leave it permanently untagged and a
    ///     permanently unclassifiable one does not get retried forever.
    /// </summary>
    public int ClassificationAttempts { get; set; }

    /// <summary>
    ///     The classifier's own confidence in the assigned types, 0–1, or <see langword="null" /> when
    ///     unclassified. Stored so a confidence threshold can be calibrated later without re-running the model.
    /// </summary>
    public double? ClassificationConfidence { get; set; }
}
