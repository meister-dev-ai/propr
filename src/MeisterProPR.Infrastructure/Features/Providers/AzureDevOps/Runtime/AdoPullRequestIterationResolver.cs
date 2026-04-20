// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Runtime;

/// <summary>Resolves the latest pull-request iteration from Azure DevOps for webhook-triggered intake.</summary>
public sealed partial class AdoPullRequestIterationResolver(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    ILogger<AdoPullRequestIterationResolver> logger) : IPullRequestIterationResolver
{
    /// <inheritdoc />
    public async Task<int> GetLatestIterationIdAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            ct);
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
        var gitClient = connection.GetClient<GitHttpClient>();
        var iterations = await gitClient.GetPullRequestIterationsAsync(
            projectId,
            repositoryId,
            pullRequestId,
            false,
            null,
            ct);
        var latestIterationId = iterations.Count > 0 ? iterations.Max(iteration => iteration.Id ?? 1) : 1;

        LogResolvedLatestIteration(logger, pullRequestId, latestIterationId);
        return latestIterationId;
    }

    [LoggerMessage(
        EventId = 2809,
        Level = LogLevel.Information,
        Message = "Resolved latest webhook iteration {IterationId} for PR #{PullRequestId}.")]
    private static partial void LogResolvedLatestIteration(ILogger logger, int pullRequestId, int iterationId);
}
