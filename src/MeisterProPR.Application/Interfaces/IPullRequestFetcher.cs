// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Interface for fetching pull request details and changed files from the source control provider.
/// </summary>
public interface IPullRequestFetcher
{
    /// <summary>
    ///     Fetches only the branch names and status of a pull request in a single lightweight call.
    ///     Use this to obtain the branch names needed for local workspace preparation before the
    ///     full content fetch.
    /// </summary>
    Task<PullRequestRef> FetchRefAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Fetches pull request details and changed files from the source control provider.
    /// </summary>
    /// <param name="organizationUrl">The URL of the organization.</param>
    /// <param name="projectId">The ID of the project.</param>
    /// <param name="repositoryId">The ID of the repository.</param>
    /// <param name="pullRequestId">The ID of the pull request.</param>
    /// <param name="iterationId">The ID of the iteration.</param>
    /// <param name="compareToIterationId">
    ///     When provided, only files changed between this iteration and
    ///     <paramref name="iterationId" /> are returned in <c>ChangedFiles</c>.
    ///     The full PR file list is still available via <c>PullRequest.AllPrFileSummaries</c>.
    ///     Pass <c>null</c> for a first-pass review where all changed files should be reviewed.
    /// </param>
    /// <param name="clientId">Optional client ID for credential retrieval.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <param name="compareToReviewRevision">
    ///     Optional provider-neutral review revision used by non-Azure DevOps adapters to compute delta files.
    ///     Pass <c>null</c> to fetch the full current pull request scope.
    /// </param>
    /// <param name="workspace">
    ///     When provided, file content is read from the local git workspace instead of downloading
    ///     it from the remote SCM API. Pass <c>null</c> to use the remote API (default behaviour).
    /// </param>
    /// <returns>A task that represents the asynchronous operation, containing the fetched pull request.</returns>
    Task<PullRequest> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        int? compareToIterationId = null,
        Guid? clientId = null,
        CancellationToken cancellationToken = default,
        ReviewRevision? compareToReviewRevision = null,
        IReviewRepositoryWorkspace? workspace = null);
}
