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
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation, containing the fetched pull request.</returns>
    Task<PullRequest> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        CancellationToken cancellationToken = default);
}