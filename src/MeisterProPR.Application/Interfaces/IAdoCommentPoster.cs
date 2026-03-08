using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Interface for posting review results as comments to Azure DevOps pull requests.
/// </summary>
public interface IAdoCommentPoster
{
    /// <summary>
    ///     Posts review results as comments to the specified pull request.
    /// </summary>
    /// <param name="organizationUrl">Base URL of the Azure DevOps organization (e.g., https://dev.azure.com/yourorg).</param>
    /// <param name="projectId">ID of the Azure DevOps project.</param>
    /// <param name="repositoryId">ID of the repository containing the pull request.</param>
    /// <param name="pullRequestId">Numeric ID of the pull request.</param>
    /// <param name="iterationId">ID of the pull request iteration to comment on.</param>
    /// <param name="result">The review result to post as a comment.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task PostAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        ReviewResult result,
        CancellationToken cancellationToken = default);
}