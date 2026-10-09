// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.Application.Features.Providers.Identity;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>Collects final thread observations and finding dispositions before pull-request measurement sealing.</summary>
/// <remarks>Active-pass archive retention is independent of this close-only collection operation.</remarks>
public sealed partial class PullRequestCloseObserver(
    IScmProviderRegistry providerRegistry,
    ILogger<PullRequestCloseObserver> logger,
    IPullRequestFetcher? pullRequestFetcher = null,
    ICodeInsightMissHarvester? missHarvester = null,
    ICodeInsightFindingStore? findingStore = null,
    ICodeInsightsCollectionGate? collectionGate = null,
    IClientScmConnectionRepository? scmConnectionRepository = null,
    IPostedCommentOriginStore? postedCommentOriginStore = null,
    IReviewerThreadStatusFetcher? reviewerThreadStatuses = null,
    ICodeInsightDispositionService? dispositionService = null,
    ICodeInsightHarvestCoverageRecorder? harvestCoverageRecorder = null) : ICodeInsightCloseObserver
{
    public async Task ObserveAsync(
        CodeInsightPullRequestKey key,
        string providerScopePath,
        string providerProjectKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Optional dependencies represent module or host availability; licensing remains a runtime collection gate.
        if (pullRequestFetcher is null
            || missHarvester is null
            || findingStore is null
            || collectionGate is null
            || scmConnectionRepository is null
            || harvestCoverageRecorder is null)
        {
            return;
        }

        string? harvestScope = null;
        var observedAt = DateTimeOffset.UtcNow;
        try
        {
            if (!await collectionGate.IsCollectionEnabledAsync(key.ClientId, ct))
            {
                return;
            }

            // Sealing candidates require collected findings. A human miss alone can create an aggregate.
            var findings = await findingStore.GetFindingsForPullRequestAsync(key, ct);
            if (findings.Count == 0)
            {
                LogNothingCollected(logger, key.PullRequestId, key.ClientId);
                return;
            }

            // Resolve the provider family before interpreting provider comment identities.
            var connection = await this.ResolveConnectionAsync(key.ClientId, providerScopePath, ct);
            if (connection is null)
            {
                // Log only the authority because source paths may contain credentials.
                LogNoConnection(
                    logger,
                    key.PullRequestId,
                    key.ClientId,
                    ScmConnectionHostMatch.ToAuthority(providerScopePath) ?? "(no host)");
                return;
            }

            var ownership = await this.ResolveOwnershipAsync(key, connection.ProviderFamily, ct);
            harvestScope = ProviderSourceIdentity.FromReviewSource(
                connection.ProviderFamily,
                providerRegistry.GetSourceIdentityPolicy(connection.ProviderFamily).SelectCapturedSource(providerScopePath, connection.HostBaseUrl),
                providerRegistry.GetSourceIdentityPolicy(connection.ProviderFamily)).Value;

            // The adapter contributes its account identity before human-thread authorship is resolved.
            var reviewerObservedAt = DateTimeOffset.UtcNow;
            var reviewerThreads = await this.ReadReviewerThreadsAsync(
                key,
                providerScopePath,
                providerProjectKey,
                ownership,
                ct);

            observedAt = DateTimeOffset.UtcNow;
            var threads = await pullRequestFetcher.FetchThreadsAsync(
                providerScopePath,
                providerProjectKey,
                key.RepositoryId,
                (int)key.PullRequestId,
                key.ClientId,
                ct);

            var coverage = new HarvestCoverageState();
            foreach (var thread in threads)
            {
                var evt = ThreadUpdatedEventFactory.Build(
                    key.ClientId, connection.Id, key.RepositoryId, key.PullRequestId, thread, ownership, observedAt, harvestScope);
                coverage.Observe(
                    evt, providerRegistry.GetCodeReviewPreparationPolicy(connection.ProviderFamily)
                        .InterpretThreadResolution(evt.Status));
                if (string.IsNullOrWhiteSpace(thread.ThreadId))
                {
                    if (ThreadUpdatedEventFactory.IsHumanThread(evt))
                    {
                        coverage.RecordRetention(false);
                    }

                    continue;
                }

                coverage.RecordRetention(await missHarvester.HandleThreadObservedAsync(evt, ct));
            }

            await this.RecordDispositionsAsync(
                key,
                providerScopePath,
                providerProjectKey,
                reviewerThreads,
                findings,
                harvestScope, reviewerObservedAt, connection.ProviderFamily,
                ct);
            await harvestCoverageRecorder.RecordAsync(
                key, harvestScope, coverage.AllHumanThreadsResolved, observedAt, ct, coverage.AllHumanObservationsRetained);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (harvestScope is not null)
            {
                await harvestCoverageRecorder.RecordAsync(key, harvestScope, false, observedAt, ct, enumerationComplete: false);
            }

            LogObservationFailed(logger, key.PullRequestId, key.ClientId, ex);
        }
    }

    /// <summary>Reads own-thread statuses and allows the adapter to contribute its authenticated account identity.</summary>
    /// <remarks>Without a status fetcher, ownership uses retained provenance and dispositions are unavailable.</remarks>
    private async Task<IReadOnlyList<PrThreadStatusEntry>> ReadReviewerThreadsAsync(
        CodeInsightPullRequestKey key,
        string providerScopePath,
        string providerProjectKey,
        ThreadOwnershipResolver ownership,
        CancellationToken ct)
    {
        if (reviewerThreadStatuses is null)
        {
            return [];
        }

        try
        {
            return await reviewerThreadStatuses.GetReviewerThreadStatusesAsync(
                providerScopePath,
                providerProjectKey,
                key.RepositoryId,
                (int)key.PullRequestId,
                ownership,
                key.ClientId,
                ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogReviewerThreadsUnavailable(logger, key.PullRequestId, key.ClientId, ex);
            return [];
        }
    }

    /// <summary>Records final outcomes for collected findings before the measurement is sealed.</summary>
    /// <remarks>Every known thread is offered; the disposition service handles replay and unresolved outcomes.</remarks>
    private async Task RecordDispositionsAsync(
        CodeInsightPullRequestKey key,
        string providerScopePath,
        string providerProjectKey,
        IReadOnlyList<PrThreadStatusEntry> reviewerThreads,
        IReadOnlyList<CodeInsightFindingView> findings,
        string providerScope,
        DateTimeOffset observedAt,
        ScmProvider provider,
        CancellationToken ct)
    {
        if (dispositionService is null || reviewerThreads.Count == 0)
        {
            return;
        }

        // Restrict disposition lookups to threads represented by collected findings.
        var findingThreads = findings
            .Select(finding => finding.ProviderThreadId)
            .Where(threadId => !string.IsNullOrWhiteSpace(threadId))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var thread in reviewerThreads)
        {
            if (string.IsNullOrWhiteSpace(thread.ThreadId) || !findingThreads.Contains(thread.ThreadId))
            {
                continue;
            }

            var intent = providerRegistry.GetCodeReviewPreparationPolicy(provider).InterpretThreadResolution(thread.Status);
            await dispositionService.HandleThreadResolvedAsync(
                new ThreadResolvedDomainEvent(
                    key.ClientId,
                    providerScopePath,
                    providerProjectKey,
                    key.RepositoryId,
                    (int)key.PullRequestId,
                    thread.ThreadId,
                    thread.FilePath,
                    null,
                    thread.CommentHistory,
                    observedAt,
                    intent,
                    thread.CodeChangedSinceRaised,
                    thread.Status, providerScope),
                ct);
        }
    }

    /// <summary>Finds the client's active connection for the host the pull request lives on.</summary>
    private async Task<ClientScmConnectionDto?> ResolveConnectionAsync(
        Guid clientId,
        string providerScopePath,
        CancellationToken ct)
    {
        var authority = ScmConnectionHostMatch.ToAuthority(providerScopePath);
        if (authority is null)
        {
            return null;
        }

        var connections = await scmConnectionRepository!.GetByClientIdAsync(clientId, ct);

        var matches = connections
            .Where(connection => connection.IsActive
                                 && !string.IsNullOrWhiteSpace(connection.HostBaseUrl)
                                 && providerRegistry.GetConnectionConfigurationPolicy(connection.ProviderFamily)
                                     .MatchesObservedConnectionHost(connection.HostBaseUrl, authority))
            .ToList();

        // Conflicting provider families on one authority prevent unambiguous comment interpretation.
        if (matches.Select(connection => connection.ProviderFamily).Distinct().Count() > 1)
        {
            LogAmbiguousConnection(logger, clientId, authority);
            return null;
        }

        return matches
            // Prefer the most specific host match when several connections share an authority.
            .OrderByDescending(connection => connection.HostBaseUrl.Length)
            .FirstOrDefault();
    }

    /// <summary>Resolves own-comment provenance without invoking a provider adapter.</summary>
    /// <remarks>The harvester checks retained finding identities when provenance is unavailable.</remarks>
    private async Task<ThreadOwnershipResolver> ResolveOwnershipAsync(
        CodeInsightPullRequestKey key,
        ScmProvider provider,
        CancellationToken ct)
    {
        if (postedCommentOriginStore is null)
        {
            return ThreadOwnershipResolver.None;
        }

        try
        {
            var provenance = await postedCommentOriginStore.GetJobIdsForPullRequestAsync(
                key.ClientId,
                key.RepositoryId,
                key.PullRequestId,
                ct);

            return ThreadOwnershipResolver.Create(
                provenance,
                ThreadOwnerIdentity.None,
                providerRegistry.GetIdentityPolicy(provider).CommentIdScope);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogOwnershipUnavailable(logger, key.PullRequestId, key.ClientId, ex);
            return ThreadOwnershipResolver.None;
        }
    }

    [LoggerMessage(
        EventId = 6105,
        Level = LogLevel.Warning,
        Message = "Reading ProPR's own threads on PR {PullRequestId} of client {ClientId} failed; its findings keep whatever outcome earlier passes recorded.")]
    private static partial void LogReviewerThreadsUnavailable(ILogger logger, long pullRequestId, Guid clientId, Exception ex);

    [LoggerMessage(
        EventId = 6102,
        Level = LogLevel.Debug,
        Message = "PR {PullRequestId} of client {ClientId} has no collected findings; its close is not observed.")]
    private static partial void LogNothingCollected(ILogger logger, long pullRequestId, Guid clientId);

    [LoggerMessage(
        EventId = 6103,
        Level = LogLevel.Warning,
        Message =
            "No active SCM connection of client {ClientId} matches {Authority}; the close of PR {PullRequestId} is sealed without observing its threads.")]
    private static partial void LogNoConnection(ILogger logger, long pullRequestId, Guid clientId, string authority);

    [LoggerMessage(
        EventId = 6104,
        Level = LogLevel.Warning,
        Message =
            "Client {ClientId} has active connections of more than one provider family on {Authority}; closes there are sealed without observing their threads.")]
    private static partial void LogAmbiguousConnection(ILogger logger, Guid clientId, string authority);

    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Warning,
        Message = "Close observation failed for PR {PullRequestId} of client {ClientId}; the measurement is sealed from the judgements already recorded.")]
    private static partial void LogObservationFailed(ILogger logger, long pullRequestId, Guid clientId, Exception ex);

    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Warning,
        Message = "Comment-origin lookup failed for PR {PullRequestId} of client {ClientId}; observing threads without ProPR's own provenance.")]
    private static partial void LogOwnershipUnavailable(ILogger logger, long pullRequestId, Guid clientId, Exception ex);
}
