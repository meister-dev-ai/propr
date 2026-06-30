// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Events;

/// <summary>
///     Raised by the review pipeline when a connection opts in to diff retention. Carries the per-file
///     canonical unified diffs observed for a single review increment so a passive archive consumer can
///     persist them without any further provider calls. Raising this event never influences review
///     decisions, deduplication, memory, or the scope snapshot.
/// </summary>
/// <param name="ClientId">The client that owns the pull request.</param>
/// <param name="ConnectionId">The SCM provider connection the pull request belongs to.</param>
/// <param name="RepositoryId">The provider repository identifier.</param>
/// <param name="PullRequestId">The provider pull-request identifier.</param>
/// <param name="RevisionKey">The stored revision key identifying the review increment.</param>
/// <param name="PullRequestState">The last-known pull-request lifecycle state.</param>
/// <param name="LastActivityAt">The UTC timestamp of the latest observed activity on the pull request.</param>
/// <param name="FileDiffs">The per-file canonical unified diffs observed for the increment.</param>
public sealed record ReviewIncrementCompletedEvent(
    Guid ClientId,
    Guid ConnectionId,
    string RepositoryId,
    long PullRequestId,
    string RevisionKey,
    string PullRequestState,
    DateTimeOffset LastActivityAt,
    IReadOnlyList<ReviewIncrementFileDiff> FileDiffs);

/// <summary>
///     A single per-file unified diff within a <see cref="ReviewIncrementCompletedEvent" />. The
///     <paramref name="UnifiedDiff" /> is the provider-neutral canonical unified diff with hunks.
/// </summary>
/// <param name="FilePath">The file path the diff applies to.</param>
/// <param name="ChangeType">The provider-neutral kind of change, e.g. "Added", "Modified", "Deleted", "Renamed".</param>
/// <param name="IsBinary">Whether the file is binary.</param>
/// <param name="UnifiedDiff">The plaintext canonical unified diff (encrypted at rest by the store).</param>
public sealed record ReviewIncrementFileDiff(
    string FilePath,
    string ChangeType,
    bool IsBinary,
    string UnifiedDiff);
