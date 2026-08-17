// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Stub;

/// <summary>
///     No-op implementation of <see cref="IActivePullRequestDiscoveryProvider" /> used when
///     <c>ADO_STUB_PR=true</c>. Always returns an empty list so the scan worker runs without hitting ADO.
/// </summary>
internal sealed partial class StubActivePrFetcher(ILogger<StubActivePrFetcher> logger)
    : IActivePullRequestDiscoveryProvider
{
    /// <inheritdoc />
    public ScmProvider Provider => ScmProvider.AzureDevOps;

    /// <inheritdoc />
    public Task<ActivePullRequestDiscovery> GetRecentlyUpdatedPullRequestsAsync(
        ActivePullRequestQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        LogStubCall(logger, query.ScopePath, query.Repositories.Count);

        // Complete rather than failed: the stub answers everything it is asked, and reporting otherwise would
        // hold the watermark of a deployment that is working exactly as configured.
        return Task.FromResult(ActivePullRequestDiscovery.Empty);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "StubActivePrFetcher: returning empty PR list for {RepositoryCount} claimed repositories in {OrganizationUrl}")]
    private static partial void LogStubCall(ILogger logger, string organizationUrl, int repositoryCount);
}
