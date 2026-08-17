// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Runtime;

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
        // An iteration is an Azure DevOps concept, and this is its only implementation, so every caller
        // reaches it whatever provider the pull request belongs to. Refused here rather than at the host:
        // a GitLab or Forgejo address has no Azure DevOps connection behind it, an absent credential is
        // answered by acquiring a token from the platform's own identity, and the call would present that
        // token to somebody else's server before failing. Callers already treat a failure as "no iteration"
        // and carry on, so this changes what leaves the process, not what they do.
        var provider = await ProviderResolutionUtilities.ResolveProviderAsync(
            organizationUrl,
            clientId,
            connectionRepository,
            ct);

        if (provider != ScmProvider.AzureDevOps)
        {
            throw new InvalidOperationException($"Pull request iterations are an Azure DevOps concept, and {organizationUrl} is a {provider} host.");
        }

        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            clientId,
            organizationUrl,
            ct);
        var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, ct);
        var gitClient = await connection.GetClientAsync<GitHttpClient>(ct);
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
