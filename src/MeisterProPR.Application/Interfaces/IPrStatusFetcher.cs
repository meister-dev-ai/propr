using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Fetches the current live status of a single pull request from the source control provider.</summary>
public interface IPrStatusFetcher
{
    /// <summary>
    ///     Returns the current ADO status of the specified pull request.
    ///     Returns <see cref="PrStatus.Active" /> on network or not-found errors so that
    ///     transient ADO unavailability does not cause false cancellations.
    /// </summary>
    /// <param name="organizationUrl">ADO organisation URL.</param>
    /// <param name="projectId">ADO project ID.</param>
    /// <param name="repositoryId">ADO repository ID.</param>
    /// <param name="pullRequestId">Numeric pull request ID.</param>
    /// <param name="clientId">Optional client ID for per-client credential resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PrStatus> GetStatusAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId,
        CancellationToken ct = default);
}
