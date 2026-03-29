using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Interface for fetching pull request details and changed files from the source control provider.
/// </summary>
public interface IPullRequestFetcher
{
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
    /// <returns>A task that represents the asynchronous operation, containing the fetched pull request.</returns>
    Task<PullRequest> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        int? compareToIterationId = null,
        Guid? clientId = null,
        CancellationToken cancellationToken = default);
}
