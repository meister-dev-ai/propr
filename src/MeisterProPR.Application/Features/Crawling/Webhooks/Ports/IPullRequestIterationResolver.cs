// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Crawling.Webhooks.Ports;

/// <summary>Resolves the latest pull-request iteration for webhook-triggered intake.</summary>
public interface IPullRequestIterationResolver
{
    /// <summary>Returns the latest iteration ID for the specified pull request.</summary>
    Task<int> GetLatestIterationIdAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default);
}
