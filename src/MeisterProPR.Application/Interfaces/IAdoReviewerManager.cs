namespace MeisterProPR.Application.Interfaces;

/// <summary>Adds an ADO identity as an optional reviewer on a pull request.</summary>
public interface IAdoReviewerManager
{
    /// <summary>
    ///     Adds the given <paramref name="reviewerId" /> as an optional reviewer on the specified pull request.
    ///     The operation is idempotent — adding an already-present reviewer is a no-op.
    /// </summary>
    /// <param name="organizationUrl">ADO organisation URL (e.g. <c>https://dev.azure.com/myorg</c>).</param>
    /// <param name="projectId">ADO project identifier.</param>
    /// <param name="repositoryId">ADO repository identifier (GUID string).</param>
    /// <param name="pullRequestId">Pull request number.</param>
    /// <param name="reviewerId">ADO identity GUID of the reviewer to add.</param>
    /// <param name="clientId">Optional client identifier used to select per-client credentials.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddOptionalReviewerAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid reviewerId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default);
}
