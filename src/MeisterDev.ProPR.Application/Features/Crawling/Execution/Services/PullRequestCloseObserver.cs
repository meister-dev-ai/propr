// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Events;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>
///     Observes a finished pull request's threads once, immediately before its measurement is sealed.
/// </summary>
/// <remarks>
///     <para>
///         Every pass that observed this pull request before now ran while it was active, so a thread that
///         reached its resolved state as part of the close has never been seen in that state. The harvester
///         judges such a thread again; a thread still open, or already judged against a resolved state, returns
///         from the harvester without a model call.
///     </para>
///     <para>
///         Harvest only. The review archive observed every thread on each active pass, and retaining another
///         copy here would store rows no retention setting asked for.
///     </para>
/// </remarks>
public sealed partial class PullRequestCloseObserver(
    ILogger<PullRequestCloseObserver> logger,
    IPullRequestFetcher? pullRequestFetcher = null,
    ICodeInsightMissHarvester? missHarvester = null,
    ICodeInsightFindingStore? findingStore = null,
    ICodeInsightsCollectionGate? collectionGate = null,
    IClientScmConnectionRepository? scmConnectionRepository = null,
    IPostedCommentOriginStore? postedCommentOriginStore = null,
    IReviewerThreadStatusFetcher? reviewerThreadStatuses = null,
    ICodeInsightDispositionService? dispositionService = null) : ICodeInsightCloseObserver
{
    public async Task ObserveAsync(
        CodeInsightPullRequestKey key,
        string providerScopePath,
        string providerProjectKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (pullRequestFetcher is null
            || missHarvester is null
            || findingStore is null
            || collectionGate is null
            || scmConnectionRepository is null)
        {
            return;
        }

        try
        {
            if (!await collectionGate.IsCollectionEnabledAsync(key.ClientId, ct))
            {
                return;
            }

            // Only a pull request ProPR reviewed and raised something on. Harvesting a miss creates the
            // code-insight aggregate when none exists, so observing any closed pull request would let one
            // human thread manufacture an aggregate with no findings, which the seal a statement later records
            // as a measurement with no true positives. The seal sweep excludes the same rows through its
            // candidate query; this is that precondition on the path that has no candidate query.
            var findings = await findingStore.GetFindingsForPullRequestAsync(key, ct);
            if (findings.Count == 0)
            {
                LogNothingCollected(logger, key.PullRequestId, key.ClientId);
                return;
            }

            // The connection carries the provider family, and the family decides whether a comment id alone
            // identifies a comment or only the thread-and-comment pair does. Reading ProPR's own comments under
            // the wrong regime would leave its own threads looking like human ones, so an unresolved connection
            // stops the observation instead of guessing. This is the same precondition the active pass has.
            var connection = await this.ResolveConnectionAsync(key.ClientId, providerScopePath, ct);
            if (connection is null)
            {
                // The reduced authority, never the scope path it came from: a path written as
                // https://user:token@host/org carries the credential, and a log line keeps it.
                LogNoConnection(
                    logger,
                    key.PullRequestId,
                    key.ClientId,
                    ScmConnectionHostMatch.ToAuthority(providerScopePath) ?? "(no host)");
                return;
            }

            var ownership = await this.ResolveOwnershipAsync(key, connection.ProviderFamily, ct);

            // Read ProPR's own threads first. The provider adapter contributes the account it authenticates as
            // into the resolver while answering, which is the second half of the ownership answer and the half
            // provenance cannot supply. Reading it before the harvest is what lets a comment ProPR posted
            // without a provenance row still be recognised as its own.
            var reviewerThreads = await this.ReadReviewerThreadsAsync(
                key,
                providerScopePath,
                providerProjectKey,
                ownership,
                ct);

            var threads = await pullRequestFetcher.FetchThreadsAsync(
                providerScopePath,
                providerProjectKey,
                key.RepositoryId,
                (int)key.PullRequestId,
                key.ClientId,
                ct);

            foreach (var thread in threads)
            {
                if (string.IsNullOrWhiteSpace(thread.ThreadId))
                {
                    continue;
                }

                var evt = ThreadUpdatedEventFactory.Build(
                    key.ClientId,
                    connection.Id,
                    key.RepositoryId,
                    key.PullRequestId,
                    thread,
                    ownership);

                await missHarvester.HandleThreadObservedAsync(evt, ct);
            }

            await this.RecordDispositionsAsync(
                key,
                providerScopePath,
                providerProjectKey,
                reviewerThreads,
                findings,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogObservationFailed(logger, key.PullRequestId, key.ClientId, ex);
        }
    }

    /// <summary>
    ///     Reads the statuses of the threads ProPR raised, and lets the provider adapter contribute the account
    ///     it posts under into <paramref name="ownership" />.
    /// </summary>
    /// <remarks>
    ///     Returns nothing when no status fetcher is registered, which leaves ownership resting on provenance
    ///     alone and the disposition side unrecorded, exactly as it was before the close observed anything.
    /// </remarks>
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

    /// <summary>
    ///     Records what became of each finding whose thread has resolved, so the close settles both sides of
    ///     the measurement it is about to seal.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every earlier pass that could record a disposition ran while the pull request was active, so a
    ///         finding whose thread resolved as part of the close has none. Counting the misses that settled at
    ///         the same moment without counting these would leave the false negatives complete and the true
    ///         positives short, which reads as a recall lower than the reviewer earned.
    ///     </para>
    ///     <para>
    ///         Every resolved thread is offered, with no comparison against a previous status. The disposition
    ///         service keeps the first decision it recorded for a finding, so a thread that already has one
    ///         costs a lookup and nothing else, and the close needs no memory of what the thread looked like on
    ///         the last pass.
    ///     </para>
    /// </remarks>
    private async Task RecordDispositionsAsync(
        CodeInsightPullRequestKey key,
        string providerScopePath,
        string providerProjectKey,
        IReadOnlyList<PrThreadStatusEntry> reviewerThreads,
        IReadOnlyList<CodeInsightFindingView> findings,
        CancellationToken ct)
    {
        if (dispositionService is null || reviewerThreads.Count == 0)
        {
            return;
        }

        // Only the threads a collected finding was raised as. The disposition service looks the finding up and
        // declines a thread it does not know, so this changes no outcome; it keeps the close from spending a
        // lookup per unrelated thread and makes the correlation something a test can assert.
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

            var intent = ThreadResolutionStatusInterpreter.InterpretIntent(thread.Status);
            if (!ThreadResolutionStatusInterpreter.IsResolved(intent))
            {
                continue;
            }

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
                    DateTimeOffset.UtcNow,
                    intent,
                    thread.CodeChangedSinceRaised),
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
                                 && ScmConnectionHostMatch.MatchesAuthority(connection.HostBaseUrl, authority))
            .ToList();

        // The provider family decides whether a comment id alone identifies a comment or only the
        // thread-and-comment pair does, and reading ProPR's own comments under the wrong regime leaves its own
        // threads looking like human ones. Two active connections on one authority disagreeing about the family
        // is not something a host match can settle, so the observation stops instead of picking one.
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

    /// <summary>
    ///     Resolves which comments on this pull request ProPR posted, from the provenance recorded when it
    ///     posted them.
    /// </summary>
    /// <remarks>
    ///     No provider adapter runs on this path, so no account identity is contributed and provenance is the
    ///     whole answer. A failed lookup falls back to owning nothing, which is the same fallback the crawl
    ///     takes; the harvester's own check against recorded finding threads still keeps ProPR's findings out.
    /// </remarks>
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
                ProviderCommentIdScopes.For(provider));
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
