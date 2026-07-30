// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Events;

/// <summary>
///     Raised by the review pipeline once a review increment's result has been persisted. Carries the
///     complete finding set the increment produced (including findings the minimum-severity filter kept
///     off the provider) so a passive consumer can materialise durable finding records without any
///     further provider call. Raising this event never influences review decisions, deduplication,
///     memory, or the scope snapshot.
/// </summary>
/// <param name="ClientId">The client that owns the pull request.</param>
/// <param name="RepositoryId">The provider repository identifier.</param>
/// <param name="PullRequestId">The provider pull-request identifier.</param>
/// <param name="JobId">The review job that produced the findings.</param>
/// <param name="RevisionKey">The stored revision key identifying the review increment.</param>
/// <param name="PullRequestState">The last-known pull-request lifecycle state.</param>
/// <param name="ObservedAt">The UTC timestamp at which the findings were observed.</param>
/// <param name="Findings">The findings the increment produced, in the order the review recorded them.</param>
/// <param name="RepositoryName">
///     The repository's display name as the provider reports it. Display only (identity stays on
///     <paramref name="RepositoryId" />) but carried because for several providers that identifier is a bare
///     number, and a number is not something anybody can pick a repository by.
/// </param>
public sealed record ReviewFindingsProducedEvent(
    Guid ClientId,
    string RepositoryId,
    long PullRequestId,
    Guid JobId,
    string RevisionKey,
    string PullRequestState,
    DateTimeOffset ObservedAt,
    IReadOnlyList<ReviewFindingProduced> Findings,
    string? RepositoryName = null);

/// <summary>
///     A single finding within a <see cref="ReviewFindingsProducedEvent" />. Anchor, severity and
///     provenance are copied from the produced review comment; the provider identifiers are set only for
///     findings this posting pass actually created a comment for, and are the join key a later
///     disposition consumer uses to recognise the finding when its thread resolves.
/// </summary>
/// <param name="Ordinal">
///     Zero-based position of the finding within the increment's finding list. Part of the natural key,
///     because two findings may legitimately share the same file and line within one increment.
/// </param>
/// <param name="FilePath">File the finding is anchored to, or <c>null</c> for pull-request-level findings.</param>
/// <param name="LineNumber">Line the finding is anchored to, or <c>null</c> when unknown.</param>
/// <param name="Severity">Severity the review assigned.</param>
/// <param name="Message">The finding text (encrypted at rest by the store).</param>
/// <param name="OriginPassKind">Review pass that produced the finding, when known.</param>
/// <param name="OriginPassIndex">1-based index of the numbered multi-pass union pass, when applicable.</param>
/// <param name="OriginPassLens">Specialist lens the producing pass ran under, when applicable.</param>
/// <param name="OriginPassShadow">Whether the finding came from a shadow pass and was never publishable.</param>
/// <param name="ScopeRelation">The finding's anchor position relative to the increment's changed ranges.</param>
/// <param name="SourceReadGrounding">Whether the reviewer read the cited line while producing the finding.</param>
/// <param name="ProviderThreadId">Provider thread the finding was posted as, when it was posted.</param>
/// <param name="ProviderCommentId">Provider comment the finding was posted as, when it was posted.</param>
/// <param name="OriginModelId">
///     The remote model that produced the finding, when a single pass owns it. Null for findings drawn from many
///     passes and for reviews that ran before the model was recorded.
/// </param>
/// <param name="OriginLogicalModelName">
///     The client's logical model name for the producing pass, when it ran through a named logical model.
/// </param>
/// <param name="OriginSymbolName">
///     Name of the definition the finding's line falls inside, resolved structurally from the file's own syntax.
///     Null for pull-request-level findings and wherever the syntax could not place the line.
/// </param>
/// <param name="OriginSymbolKind">What kind of definition that is (method, class, function, …).</param>
public sealed record ReviewFindingProduced(
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
