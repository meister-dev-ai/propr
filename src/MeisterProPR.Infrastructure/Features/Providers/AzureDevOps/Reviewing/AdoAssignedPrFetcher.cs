// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Globalization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>ADO-backed implementation of <see cref="IAssignedReviewDiscoveryService" />.</summary>
public sealed partial class AdoAssignedPrFetcher(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    ILogger<AdoAssignedPrFetcher> logger) : IAssignedReviewDiscoveryService
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    /// <summary>
    ///     Resolves a <see cref="GitHttpClient" /> for the given organization URL.
    ///     Exposed as a delegate so tests can inject a mock without requiring a real VssConnection.
    /// </summary>
    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssignedCodeReviewRef>> ListAssignedOpenReviewsAsync(
        CrawlConfigurationDto config,
        CancellationToken cancellationToken = default)
    {
        if (config.ReviewerId is null)
        {
            LogSkippedNoReviewerIdentity(logger, config.Id, config.ClientId);
            return [];
        }

        using var activity = ActivitySource.StartActivity("AdoAssignedPrFetcher.GetAssignedOpenPullRequests");
        activity?.SetTag("ado.org", config.ProviderScopePath);
        activity?.SetTag("ado.project", config.ProviderProjectKey);

        var gitClient = await this.ResolveGitClientAsync(config, cancellationToken);
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, config.ProviderScopePath);

        var criteria = new GitPullRequestSearchCriteria
        {
            ReviewerId = config.ReviewerId,
            Status = PullRequestStatus.Active,
        };

        var prs = await gitClient.GetPullRequestsByProjectAsync(
            config.ProviderProjectKey,
            criteria,
            top: 200,
            userState: null,
            cancellationToken: cancellationToken);

        activity?.SetTag("ado.prs_found", prs.Count);

        // Apply repo and branch filters when the config specifies any.
        if (config.RepoFilters.Count > 0)
        {
            prs = prs
                .Where(pr => MatchesRepoFilters(config.RepoFilters, pr))
                .ToList();
            activity?.SetTag("ado.prs_after_filter", prs.Count);
        }

        var results = new List<AssignedCodeReviewRef>(prs.Count);
        foreach (var pr in prs)
        {
            try
            {
                var repositoryId = pr.Repository?.Id.ToString();
                if (string.IsNullOrWhiteSpace(repositoryId))
                {
                    continue;
                }

                var iterations = await gitClient.GetPullRequestIterationsAsync(
                    config.ProviderProjectKey,
                    repositoryId,
                    pr.PullRequestId,
                    false,
                    null,
                    cancellationToken);

                var latestIteration = iterations.Count > 0 ? iterations.Max(i => i.Id ?? 1) : 1;
                ReviewRevision? reviewRevision = null;
                try
                {
                    reviewRevision = await CreateReviewRevisionAsync(
                        gitClient,
                        config.ProviderProjectKey,
                        repositoryId,
                        pr.PullRequestId,
                        latestIteration,
                        cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    LogReviewRevisionFetchWarning(logger, pr.PullRequestId, latestIteration, ex);
                }

                var repository = new RepositoryRef(
                    host,
                    repositoryId,
                    config.ProviderProjectKey,
                    config.ProviderProjectKey);
                var review = new CodeReviewRef(
                    repository,
                    CodeReviewPlatformKind.PullRequest,
                    pr.PullRequestId.ToString(CultureInfo.InvariantCulture),
                    pr.PullRequestId);

                results.Add(
                    new AssignedCodeReviewRef(
                        host,
                        repository,
                        review,
                        latestIteration,
                        pr.Title,
                        pr.Repository?.Name,
                        StripRefsHeads(pr.SourceRefName ?? string.Empty),
                        StripRefsHeads(pr.TargetRefName ?? string.Empty),
                        reviewRevision));
            }
            catch (Exception ex)
            {
                LogIterationFetchWarning(logger, pr.PullRequestId, ex);
            }
        }

        return results;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to get iterations for PR #{PrId}")]
    private static partial void LogIterationFetchWarning(ILogger logger, int prId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Failed to enrich PR #{PrId} with revision details for iteration {IterationId}; continuing without ReviewRevision snapshot")]
    private static partial void LogReviewRevisionFetchWarning(ILogger logger, int prId, int iterationId, Exception ex);

    [LoggerMessage(
        EventId = 5002,
        Level = LogLevel.Warning,
        Message = "Skipping crawl config {ConfigId} for client {ClientId} — reviewer identity not configured")]
    private static partial void LogSkippedNoReviewerIdentity(
        ILogger logger,
        Guid configId,
        Guid clientId);

    private async Task<GitHttpClient> ResolveGitClientAsync(CrawlConfigurationDto config, CancellationToken ct)
    {
        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            config.ClientId,
            config.ProviderScopePath,
            ct);

        if (this.GitClientResolver is not null)
        {
            return await this.GitClientResolver(config.ProviderScopePath, ct);
        }

        var connection = await connectionFactory.GetConnectionAsync(config.ProviderScopePath, credentials, ct);
        return connection.GetClient<GitHttpClient>();
    }

    private static async Task<ReviewRevision?> CreateReviewRevisionAsync(
        GitHttpClient gitClient,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int latestIterationId,
        CancellationToken ct)
    {
        if (latestIterationId <= 0)
        {
            return null;
        }

        var latestIteration = await gitClient.GetPullRequestIterationAsync(
            projectId,
            repositoryId,
            pullRequestId,
            latestIterationId,
            cancellationToken: ct);

        var headSha = latestIteration.SourceRefCommit?.CommitId;
        var baseSha = latestIteration.CommonRefCommit?.CommitId;
        if (string.IsNullOrWhiteSpace(headSha) || string.IsNullOrWhiteSpace(baseSha))
        {
            return null;
        }

        var providerRevisionId = latestIterationId.ToString(CultureInfo.InvariantCulture);
        return new ReviewRevision(headSha, baseSha, baseSha, providerRevisionId, $"{baseSha}...{headSha}");
    }

    /// <summary>
    ///     Returns true if the PR matches at least one repo filter (and its optional branch patterns).
    /// </summary>
    private static bool MatchesRepoFilters(
        IReadOnlyList<CrawlRepoFilterDto> filters,
        GitPullRequest pr)
    {
        var repoName = pr.Repository?.Name ?? string.Empty;

        foreach (var filter in filters)
        {
            if (!string.Equals(filter.RepositoryName, repoName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Repo matched — check branch patterns (empty list = all branches accepted).
            if (filter.TargetBranchPatterns.Count == 0)
            {
                return true;
            }

            var targetBranch = StripRefsHeads(pr.TargetRefName ?? string.Empty);
            return MatchesBranchPatterns(filter.TargetBranchPatterns, targetBranch);
        }

        return false;
    }

    private static string StripRefsHeads(string branch)
    {
        return branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch[11..] : branch;
    }

    private static bool MatchesBranchPatterns(IReadOnlyList<string> patterns, string branch)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(patterns);
        return matcher.Match(branch).HasMatches;
    }
}
