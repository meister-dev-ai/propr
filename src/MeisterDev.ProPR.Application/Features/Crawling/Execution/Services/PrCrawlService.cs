// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Services;

/// <summary>Orchestrates the periodic PR crawl: discovers assigned PRs and creates pending review jobs.</summary>
public sealed partial class PrCrawlService(
    ICrawlConfigurationRepository crawlConfigs,
    IAssignedReviewDiscoveryService prFetcher,
    IJobRepository jobs,
    IPrStatusFetcher prStatusFetcher,
    ILogger<PrCrawlService> logger,
    IPullRequestSynchronizationService pullRequestSynchronizationService,
    IProviderActivationService? providerActivationService = null,
    IClientRegistry? clientRegistry = null,
    IScmProviderRegistry? providerRegistry = null) : IPrCrawlService
{
    /// <summary>
    ///     Runs one crawl cycle across all active configurations, creating review jobs for newly discovered pull requests.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the crawl cycle.</param>
    public async Task CrawlAsync(CancellationToken cancellationToken = default)
    {
        var configs = await crawlConfigs.GetAllActiveAsync(cancellationToken);
        LogCrawlStarted(logger, configs.Count);

        foreach (var config in configs)
        {
            if (providerActivationService is not null &&
                !await providerActivationService.IsEnabledAsync(config.Provider, cancellationToken))
            {
                continue;
            }

            IReadOnlyList<AssignedCodeReviewRef> assignedPrs;
            ResolvedReviewer reviewerContext;
            try
            {
                assignedPrs = await prFetcher.ListAssignedOpenReviewsAsync(config, cancellationToken);
                reviewerContext = await this.ResolveReviewerContextAsync(config, cancellationToken);
                LogPrsDiscovered(logger, assignedPrs.Count, config.ProviderScopePath, config.ProviderProjectKey);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogConfigFetchError(logger, config.ProviderScopePath, config.ProviderProjectKey, ex);
                continue;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await this.ProcessDiscoveredPullRequestsAsync(config, assignedPrs, reviewerContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTargetAdmissionError(logger, config.Id, ex);
            }

            await this.DetectAbandonedJobsAsync(config, assignedPrs, cancellationToken);
        }
    }

    private async Task ProcessDiscoveredPullRequestsAsync(
        CrawlConfigurationDto config,
        IReadOnlyList<AssignedCodeReviewRef> assignedPrs,
        ResolvedReviewer reviewerContext,
        CancellationToken cancellationToken)
    {
        foreach (var pr in assignedPrs)
        {
            try
            {
                var canonical = config.RepoFilters.Count == 1 && config.RepoFilters[0].CanonicalSourceRef is not null;
                var request = new PullRequestSynchronizationRequest
                {
                    ActivationSource = PullRequestActivationSource.Crawl,
                    SummaryLabel = "crawl discovery",
                    ClientId = config.ClientId,
                    ProviderScopePath = config.ProviderScopePath,
                    ProviderProjectKey = config.ProviderProjectKey,
                    RepositoryId = pr.Repository.ExternalRepositoryId,
                    PullRequestId = pr.CodeReview.Number,
                    PullRequestStatus = PrStatus.Active,
                    Provider = pr.Host.Provider,
                    Host = pr.Host,
                    Repository = pr.Repository,
                    CodeReview = pr.CodeReview,
                    ReviewRevision = pr.ReviewRevision,
                    RequestedReviewerIdentity = reviewerContext.ConfiguredTriggerReviewer,
                    CandidateIterationId = pr.RevisionId,
                    PrTitle = pr.ReviewTitle,
                    RepositoryName = pr.RepositoryDisplayName,
                    SourceBranch = pr.SourceBranch,
                    TargetBranch = pr.TargetBranch,
                    ProCursorSourceScopeMode = config.ProCursorSourceScopeMode,
                    ProCursorSourceIds = config.ProCursorSourceIds ?? [],
                    InvalidProCursorSourceIds = config.InvalidProCursorSourceIds ?? [],
                    ReviewTemperature = config.ReviewTemperature,
                };
                if (!canonical)
                {
                    if (await this.GetCurrentAdmissionConfigurationAsync(config, pr, cancellationToken).ConfigureAwait(false) is not null)
                    {
                        await this.TrySynchronizeAsync(request, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var prepared = await pullRequestSynchronizationService.PrepareAsync(request, cancellationToken).ConfigureAwait(false);
                await using var admission = await crawlConfigs.AcquireReviewTargetAdmissionAsync(config.ClientId, cancellationToken).ConfigureAwait(false);
                var current = await this.GetCurrentAdmissionConfigurationAsync(config, pr, cancellationToken).ConfigureAwait(false);
                if (current is not null)
                {
                    await prepared.CompleteAsync(
                        new PullRequestReviewSettings(
                            current.ProCursorSourceScopeMode,
                            current.ProCursorSourceIds ?? [], current.InvalidProCursorSourceIds ?? [], current.ReviewTemperature), cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTargetAdmissionError(logger, config.Id, ex);
            }
        }
    }

    private async Task<CrawlConfigurationDto?> GetCurrentAdmissionConfigurationAsync(
        CrawlConfigurationDto discovered,
        AssignedCodeReviewRef pr,
        CancellationToken ct)
    {
        var canonical = discovered.RepoFilters.Count == 1 && discovered.RepoFilters[0].CanonicalSourceRef is not null;
        var current = canonical
            ? await crawlConfigs.GetReviewTargetPolicySnapshotAsync(discovered.Id, discovered.ClientId, ct).ConfigureAwait(false)
            : discovered;
        if (current is null || current.ClientId != discovered.ClientId || current.Id != discovered.Id)
        {
            return null;
        }

        if (!current.IsActive || current.ReviewTargetLifecycle != ReviewTargetLifecycle.Enabled)
        {
            return null;
        }

        if (current.ReviewTargetRevision != discovered.ReviewTargetRevision)
        {
            return null;
        }

        if (!canonical)
        {
            return current;
        }

        if (current.Provider != discovered.Provider || current.Provider != pr.Host.Provider)
        {
            return null;
        }

        var currentHost = new ProviderHostRef(current.Provider, current.ProviderScopePath);
        if (!Equals(currentHost, pr.Host) || !Equals(pr.Host, pr.Repository.Host))
        {
            return null;
        }

        var scopeUnchanged = string.Equals(current.ProviderScopePath, discovered.ProviderScopePath, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(current.ProviderProjectKey, discovered.ProviderProjectKey, StringComparison.OrdinalIgnoreCase);
        if (!scopeUnchanged)
        {
            return null;
        }

        if (current.RepoFilters.Count != 1 || current.RepoFilters[0].CanonicalSourceRef is null)
        {
            return null;
        }

        if (!Equals(current.RepoFilters[0].CanonicalSourceRef, discovered.RepoFilters[0].CanonicalSourceRef))
        {
            return null;
        }

        var filter = current.RepoFilters[0];
        if (providerRegistry is null)
        {
            return null;
        }

        var repositoryMatches = providerRegistry.GetReviewSourcePolicy(current.Provider).MatchesRepository(current, pr.Repository);

        if (!repositoryMatches || !DestinationBranchPolicy.TryCreate(filter.TargetBranchPatterns, out var policy))
        {
            return null;
        }

        return policy!.Matches(pr.TargetBranch) ? current : null;
    }

    // Check live lifecycle state for active jobs absent from discovery and forward it
    // to shared synchronization without admitting another review.
    private async Task DetectAbandonedJobsAsync(
        CrawlConfigurationDto config,
        IReadOnlyList<AssignedCodeReviewRef> assignedPrs,
        CancellationToken cancellationToken)
    {
        var activeJobs = await jobs.GetActiveJobsForConfigAsync(
            config.ClientId,
            config.ProviderScopePath,
            config.ProviderProjectKey,
            cancellationToken);
        var repositoryIds = config.RepoFilters.Count > 0 && config.RepoFilters.All(filter => filter.CanonicalSourceRef is not null)
            ? config.RepoFilters.Select(filter => filter.CanonicalSourceRef!.Value).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var discoveredPrIds = assignedPrs
            .Select(pr => (pr.Repository.ExternalRepositoryId.ToUpperInvariant(), pr.CodeReview.Number))
            .ToHashSet();

        foreach (var activeJob in activeJobs)
        {
            if (activeJob.ClientId != config.ClientId ||
                (repositoryIds is not null && !repositoryIds.Contains(activeJob.RepositoryId)) ||
                discoveredPrIds.Contains((activeJob.RepositoryId.ToUpperInvariant(), activeJob.PullRequestId)))
            {
                continue;
            }

            LogAbandonmentCheckStarted(logger, activeJob.Id, activeJob.PullRequestId);

            var status = await prStatusFetcher.GetStatusAsync(
                config.ProviderScopePath,
                config.ProviderProjectKey,
                activeJob.RepositoryId,
                activeJob.PullRequestId,
                config.ClientId,
                cancellationToken);

            await this.TrySynchronizeAsync(
                new PullRequestSynchronizationRequest
                {
                    ActivationSource = PullRequestActivationSource.Crawl,
                    SummaryLabel = "crawl disappearance",
                    ClientId = config.ClientId,
                    ProviderScopePath = config.ProviderScopePath,
                    ProviderProjectKey = config.ProviderProjectKey,
                    RepositoryId = activeJob.RepositoryId,
                    PullRequestId = activeJob.PullRequestId,
                    PullRequestStatus = status,

                    // Synchronization uses the captured family to select the client's provider connection.
                    Provider = config.Provider,
                    AllowReviewSubmission = false,
                },
                cancellationToken);
        }
    }

    private async Task TrySynchronizeAsync(PullRequestSynchronizationRequest request, CancellationToken ct)
    {
        try
        {
            await pullRequestSynchronizationService.SynchronizeAsync(request, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogSynchronizationFailed(
                logger,
                request.PullRequestId,
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.SummaryLabel,
                ex);
        }
    }

    private async Task<ResolvedReviewer> ResolveReviewerContextAsync(
        CrawlConfigurationDto config,
        CancellationToken ct)
    {
        if (clientRegistry is null)
        {
            return new ResolvedReviewer(null);
        }

        var host = new ProviderHostRef(config.Provider, config.ProviderScopePath);
        return new ResolvedReviewer(await clientRegistry.GetReviewerIdentityAsync(config.ClientId, host, ct));
    }

    private sealed record ResolvedReviewer(ReviewerIdentity? ConfiguredTriggerReviewer);
}
