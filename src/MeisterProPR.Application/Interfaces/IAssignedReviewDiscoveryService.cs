// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Discovers provider-backed review requests assigned to the configured reviewer for one crawl configuration.</summary>
public interface IAssignedReviewDiscoveryService
{
    /// <summary>Lists open review requests that are currently assigned to the crawl configuration's reviewer.</summary>
    Task<IReadOnlyList<AssignedCodeReviewRef>> ListAssignedOpenReviewsAsync(
        CrawlConfigurationDto config,
        CancellationToken cancellationToken = default);
}
