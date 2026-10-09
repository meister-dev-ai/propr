// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Fetches the current live status of a single pull request from the source control provider.</summary>
public interface IPrStatusFetcher
{
    /// <summary>
    ///     Returns the current provider status of the specified pull request.
    ///     Returns <see cref="PrStatus.Active" /> on network or not-found errors so that
    ///     transient provider unavailability does not cause false cancellations.
    /// </summary>
    /// <param name="organizationUrl">Provider scope URL.</param>
    /// <param name="projectId">Provider project key.</param>
    /// <param name="repositoryId">Provider repository identifier.</param>
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
