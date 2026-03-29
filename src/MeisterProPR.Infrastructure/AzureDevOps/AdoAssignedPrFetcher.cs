using System.Diagnostics;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>ADO-backed implementation of <see cref="IAssignedPrFetcher" />.</summary>
public sealed partial class AdoAssignedPrFetcher(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository,
    ILogger<AdoAssignedPrFetcher> logger) : IAssignedPrFetcher
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    /// <summary>
    ///     Resolves a <see cref="GitHttpClient" /> for the given organization URL.
    ///     Exposed as a delegate so tests can inject a mock without requiring a real VssConnection.
    /// </summary>
    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssignedPullRequestRef>> GetAssignedOpenPullRequestsAsync(
        CrawlConfigurationDto config,
        CancellationToken cancellationToken = default)
    {
        if (config.ReviewerId is null)
        {
            LogSkippedNoReviewerIdentity(logger, config.Id, config.ClientId);
            return [];
        }

        using var activity = ActivitySource.StartActivity("AdoAssignedPrFetcher.GetAssignedOpenPullRequests");
        activity?.SetTag("ado.org", config.OrganizationUrl);
        activity?.SetTag("ado.project", config.ProjectId);

        var gitClient = await this.ResolveGitClientAsync(config, cancellationToken);

        var criteria = new GitPullRequestSearchCriteria
        {
            ReviewerId = config.ReviewerId,
            Status = PullRequestStatus.Active,
        };

        var prs = await gitClient.GetPullRequestsByProjectAsync(
            config.ProjectId,
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

        var results = new List<AssignedPullRequestRef>(prs.Count);
        foreach (var pr in prs)
        {
            try
            {
                var iterations = await gitClient.GetPullRequestIterationsAsync(
                    config.ProjectId,
                    pr.Repository.Id.ToString(),
                    pr.PullRequestId,
                    false,
                    null,
                    cancellationToken);

                var latestIteration = iterations.Count > 0 ? iterations.Max(i => i.Id ?? 1) : 1;

                results.Add(
                    new AssignedPullRequestRef(
                        config.OrganizationUrl,
                        config.ProjectId,
                        pr.Repository.Id.ToString(),
                        pr.PullRequestId,
                        latestIteration,
                        PrTitle: pr.Title,
                        RepositoryName: pr.Repository?.Name,
                        SourceBranch: StripRefsHeads(pr.SourceRefName ?? string.Empty),
                        TargetBranch: StripRefsHeads(pr.TargetRefName ?? string.Empty)));
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
        EventId = 5002,
        Level = LogLevel.Warning,
        Message = "Skipping crawl config {ConfigId} for client {ClientId} — reviewer identity not configured")]
    private static partial void LogSkippedNoReviewerIdentity(
        ILogger logger,
        Guid configId,
        Guid clientId);

    private async Task<GitHttpClient> ResolveGitClientAsync(CrawlConfigurationDto config, CancellationToken ct)
    {
        var credentials = await credentialRepository.GetByClientIdAsync(config.ClientId, ct);

        if (this.GitClientResolver is not null)
        {
            return await this.GitClientResolver(config.OrganizationUrl, ct);
        }

        var connection = await connectionFactory.GetConnectionAsync(config.OrganizationUrl, credentials, ct);
        return connection.GetClient<GitHttpClient>();
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

    private static string StripRefsHeads(string branch) =>
        branch.StartsWith("refs/heads/", StringComparison.Ordinal) ? branch[11..] : branch;

    private static bool MatchesBranchPatterns(IReadOnlyList<string> patterns, string branch)
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(patterns);
        return matcher.Match(branch).HasMatches;
    }
}
