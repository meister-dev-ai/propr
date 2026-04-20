// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Stub;

/// <summary>No-op implementation of <see cref="IAssignedReviewDiscoveryService" /> for dev/stub mode.</summary>
public sealed class StubAssignedPrFetcher : IAssignedReviewDiscoveryService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<AssignedCodeReviewRef>> ListAssignedOpenReviewsAsync(
        CrawlConfigurationDto config,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<AssignedCodeReviewRef>>(Array.Empty<AssignedCodeReviewRef>());
    }
}
