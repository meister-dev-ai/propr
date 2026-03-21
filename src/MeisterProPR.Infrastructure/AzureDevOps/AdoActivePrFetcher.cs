using System.Diagnostics;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     ADO implementation of <see cref="IActivePrFetcher" />.
///     Fetches all active pull requests for mention scanning.
///     The <paramref name="updatedAfter" /> hint is accepted by the interface but intentionally
///     NOT forwarded to ADO as a <c>minTime</c> filter: the ADO REST API's <c>minTime</c>
///     parameter filters on CREATION date, not last-update date. Filtering out old-but-active
///     PRs would cause mentions in long-running PRs to be silently missed.
///     Per-PR comment watermarks in <see cref="MeisterProPR.Application.Services.MentionScanService" />
///     handle efficient re-scan deduplication.
/// </summary>
internal sealed partial class AdoActivePrFetcher(
    VssConnectionFactory connectionFactory,
    IClientAdoCredentialRepository credentialRepository,
    ILogger<AdoActivePrFetcher> logger) : IActivePrFetcher
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.Infrastructure");

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActivePullRequestRef>> GetRecentlyUpdatedPullRequestsAsync(
        string organizationUrl,
        string projectId,
        DateTimeOffset updatedAfter,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var activity = ActivitySource.StartActivity("AdoActivePrFetcher.GetRecentlyUpdatedPullRequests");
            activity?.SetTag("ado.organization_url", organizationUrl);
            activity?.SetTag("ado.project_id", projectId);

            var credentials = clientId.HasValue
                ? await credentialRepository.GetByClientIdAsync(clientId.Value, cancellationToken)
                : null;
            var connection = await connectionFactory.GetConnectionAsync(organizationUrl, credentials, cancellationToken);
            var gitClient = connection.GetClient<GitHttpClient>();

            // Intentionally no MinTime filter: ADO's minTime filters on creation date,
            // not last-update date. Old active PRs that gain a new @mention would be
            // silently skipped if a creation-date cutoff were applied.
            var criteria = new GitPullRequestSearchCriteria
            {
                Status = PullRequestStatus.Active,
            };

            var prs = await gitClient.GetPullRequestsByProjectAsync(
                projectId,
                criteria,
                top: 500,
                userState: null,
                cancellationToken: cancellationToken);

            LogPrsFetched(logger, organizationUrl, projectId, prs.Count);

            return prs
                .Select(pr => new ActivePullRequestRef(
                    organizationUrl,
                    projectId,
                    pr.Repository.Id.ToString(),
                    pr.PullRequestId,
                    // ADO SDK does not expose a lastUpdateDate on GitPullRequest; use UtcNow
                    // as an upper-bound so the PR-level skip check in MentionScanService
                    // falls back to evaluating actual comment timestamps.
                    DateTimeOffset.UtcNow))
                .ToList()
                .AsReadOnly();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFetchError(logger, organizationUrl, projectId, ex);
            return [];
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "AdoActivePrFetcher: fetched {Count} active PRs for {OrganizationUrl}/{ProjectId}")]
    private static partial void LogPrsFetched(ILogger logger, string organizationUrl, string projectId, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "AdoActivePrFetcher: failed to fetch PRs for {OrganizationUrl}/{ProjectId}")]
    private static partial void LogFetchError(ILogger logger, string organizationUrl, string projectId, Exception ex);
}
