// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Fetches recently updated active pull requests for mention scanning.
/// </summary>
public interface IActivePrFetcher
{
    /// <summary>
    ///     Fetches all active pull requests in a project that were updated
    ///     at or after <paramref name="updatedAfter" />.
    /// </summary>
    /// <param name="organizationUrl">ADO organization URL.</param>
    /// <param name="projectId">ADO project identifier.</param>
    /// <param name="updatedAfter">
    ///     Minimum last-update timestamp. Passed as <c>minLastUpdateDate</c> to the ADO PR list query.
    /// </param>
    /// <param name="clientId">Optional client ID for per-client credential retrieval.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A read-only list of recently updated active pull request references.</returns>
    Task<IReadOnlyList<ActivePullRequestRef>> GetRecentlyUpdatedPullRequestsAsync(
        string organizationUrl,
        string projectId,
        DateTimeOffset updatedAfter,
        Guid? clientId = null,
        CancellationToken cancellationToken = default);
}
