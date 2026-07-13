// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Provider-neutral capability that retrieves the work items (Azure DevOps) or issues
///     (GitHub, GitLab, Forgejo) linked to a pull request, plus the on-demand detail lookups the
///     review model can request. One implementation per <see cref="ScmProvider" />, resolved through
///     <see cref="IScmProviderRegistry.GetLinkedItemProvider" />.
/// </summary>
/// <remarks>
///     All members must fail soft: a provider error (auth, rate limit, missing or inaccessible item)
///     yields an empty result rather than an exception, so a review is never failed by linked-item
///     retrieval. Fetches use the client's existing authenticated connection identity; items the
///     connection cannot see are simply absent.
/// </remarks>
public interface ILinkedItemProvider
{
    /// <summary>The SCM provider family this implementation serves.</summary>
    ScmProvider Provider { get; }

    /// <summary>
    ///     Discovers the items linked to the given pull request using the provider's native link
    ///     mechanism and returns a bounded, provider-neutral summary for each (deduplicated).
    /// </summary>
    Task<IReadOnlyList<LinkedItem>> DiscoverLinkedItemsAsync(
        Guid clientId,
        PullRequest pullRequest,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Retrieves the structured fields of a single linked item (e.g. state, acceptance criteria).
    ///     Returns <c>null</c> when the item cannot be found or accessed.
    /// </summary>
    Task<LinkedItemDetails?> GetItemDetailsAsync(
        Guid clientId,
        PullRequest pullRequest,
        string providerKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Retrieves the discussion/comment thread of a single linked item, oldest first.
    ///     Returns an empty list when the item has no discussion or cannot be accessed.
    /// </summary>
    Task<IReadOnlyList<LinkedItemComment>> GetItemDiscussionAsync(
        Guid clientId,
        PullRequest pullRequest,
        string providerKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolves a related link surfaced on a discovered item into a full linked-item summary.
    ///     Returns <c>null</c> when the target cannot be found or accessed.
    /// </summary>
    Task<LinkedItem?> ResolveRelatedLinkAsync(
        Guid clientId,
        PullRequest pullRequest,
        string relatedTargetKey,
        CancellationToken cancellationToken = default);
}
