// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Fetches open pull requests from Azure DevOps that are assigned to the service account reviewer.
/// </summary>
public interface IAssignedPrFetcher
{
    /// <summary>
    ///     Returns all currently open pull requests in the given crawl configuration's project
    ///     where the configured service account is listed as a reviewer.
    /// </summary>
    Task<IReadOnlyList<AssignedPullRequestRef>> GetAssignedOpenPullRequestsAsync(
        CrawlConfigurationDto config,
        CancellationToken cancellationToken = default);
}
