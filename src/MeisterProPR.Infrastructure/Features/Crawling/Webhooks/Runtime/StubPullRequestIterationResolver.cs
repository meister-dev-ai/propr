// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;

namespace MeisterProPR.Infrastructure.Features.Crawling.Webhooks.Runtime;

/// <summary>Testing and local-development stub for webhook-triggered iteration resolution.</summary>
public sealed class StubPullRequestIterationResolver : IPullRequestIterationResolver
{
    /// <inheritdoc />
    public Task<int> GetLatestIterationIdAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        return Task.FromResult(1);
    }
}
