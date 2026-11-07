// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Runtime;

/// <summary>
///     Compares the latest Azure DevOps iteration of a pull request with the iteration a thread pass was queued at.
/// </summary>
/// <remarks>
///     Azure DevOps numbers the iterations of a pull request in ascending order, so a later revision has a higher
///     iteration number. The iteration resolver is registered with the crawling services. In a composition without
///     it, the reader reports no later revision and the pass runs at the iteration it was queued at.
/// </remarks>
internal sealed class AdoLatestRevisionReader(IPullRequestIterationResolver? iterationResolver = null) : ILatestRevisionReader
{
    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public async Task<bool> HasMovedPastAsync(ThreadPassJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (iterationResolver is null)
        {
            return false;
        }

        var latestIterationId = await iterationResolver.GetLatestIterationIdAsync(
            job.ClientId,
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            ct);
        return latestIterationId > job.IterationId;
    }
}
