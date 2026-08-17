// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Base class for the per-provider discovery adapters. Resolves one connection per tick, iterates the
///     claimed repositories, and applies the shared error handling.
/// </summary>
/// <remarks>
///     The error handling is here rather than in each adapter because it is identical for all of them and its
///     failure modes are silent. A repository that was deleted, renamed or is no longer visible must not stop
///     the other repositories from being read. A rate limit must stop the tick, because the remaining
///     repositories would hit the same limit. Both cases mark the result incomplete so the caller does not
///     advance its watermark over what was not read.
/// </remarks>
/// <typeparam name="TContext">What the adapter resolves once before reading any repository, such as an
///     authenticated connection.</typeparam>
internal abstract class ActivePullRequestDiscoveryProviderBase<TContext>(ILogger logger)
    : IActivePullRequestDiscoveryProvider
{
    /// <inheritdoc />
    public abstract ScmProvider Provider { get; }

    /// <inheritdoc />
    public async Task<ActivePullRequestDiscovery> GetRecentlyUpdatedPullRequestsAsync(
        ActivePullRequestQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // A configuration with no claimed repositories has nothing to query, so no connection is opened.
        // Reported complete: there was nothing to read.
        if (query.Repositories.Count == 0)
        {
            return ActivePullRequestDiscovery.Empty;
        }

        TContext context;
        try
        {
            context = await this.PrepareAsync(query, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ActivePullRequestDiscoveryLog.ConnectionFailed(logger, this.Provider, query.ScopePath, ex);
            return ActivePullRequestDiscovery.Failed;
        }

        var discovered = new List<ActivePullRequestRef>();
        var covered = true;

        foreach (var repository in query.Repositories)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                covered = false;
                break;
            }

            try
            {
                discovered.AddRange(await this.ListRepositoryAsync(context, query, repository, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && this.IsThrottled(ex))
            {
                // The remaining repositories would hit the same limit, so the tick stops here. What was
                // read is returned, marked incomplete so the caller keeps its window open.
                ActivePullRequestDiscoveryLog.Throttled(
                    logger,
                    this.Provider,
                    query.ScopePath,
                    repository.RepositoryId);
                covered = false;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A repository that was deleted, renamed or is no longer visible to the connection does
                // not stop the others being read. The tick is still incomplete, because a mention in the
                // repository that failed has not been seen.
                ActivePullRequestDiscoveryLog.RepositoryFailed(
                    logger,
                    this.Provider,
                    query.ScopePath,
                    repository.RepositoryId,
                    ex);
                covered = false;
            }
        }

        ActivePullRequestDiscoveryLog.Discovered(
            logger,
            this.Provider,
            query.ScopePath,
            query.Repositories.Count,
            discovered.Count);

        return new ActivePullRequestDiscovery(discovered.AsReadOnly(), covered);
    }

    /// <summary>Resolves whatever this adapter needs once, before it reads any claimed repository.</summary>
    protected abstract Task<TContext> PrepareAsync(ActivePullRequestQuery query, CancellationToken ct);

    /// <summary>Lists the open pull requests in one claimed repository that are newer than the watermark.</summary>
    protected abstract Task<IReadOnlyList<ActivePullRequestRef>> ListRepositoryAsync(
        TContext context,
        ActivePullRequestQuery query,
        ClaimedRepositoryRef repository,
        CancellationToken ct);

    /// <summary>Reports whether a failure is this provider saying "too fast" rather than "no".</summary>
    protected virtual bool IsThrottled(Exception exception)
    {
        return ProviderThrottleSignal.IsThrottled(exception);
    }
}

/// <summary>The log entries the discovery adapters write.</summary>
/// <remarks>
///     Declared outside the generic base class because the logging source generator does not support a generic
///     containing type. One shared set of entries also keeps the messages identical across providers.
/// </remarks>
internal static partial class ActivePullRequestDiscoveryLog
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Provider} mention discovery could not reach {ScopePath}")]
    internal static partial void ConnectionFailed(
        ILogger logger,
        ScmProvider provider,
        string scopePath,
        Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Provider} mention discovery failed for repository {RepositoryId} in {ScopePath}")]
    internal static partial void RepositoryFailed(
        ILogger logger,
        ScmProvider provider,
        string scopePath,
        string repositoryId,
        Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "{Provider} throttled mention discovery at repository {RepositoryId} in {ScopePath}; the remaining claimed repositories wait for the next scan")]
    internal static partial void Throttled(
        ILogger logger,
        ScmProvider provider,
        string scopePath,
        string repositoryId);

    /// <summary>
    ///     Logged when a repository's listing stops at the page limit, because the result is otherwise
    ///     indistinguishable from having read every page.
    /// </summary>
    /// <remarks>
    ///     The tick is not marked incomplete for this. Keeping the window open would re-read the same capped
    ///     pages on every later tick without ever reaching the pull requests past them. The limit is far above
    ///     the number of pull requests a repository has open and updated since the previous tick.
    /// </remarks>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "{Provider} mention discovery stopped at its {PageCap}-page limit for repository {RepositoryId}; pull requests past it are not scanned this tick")]
    internal static partial void PageLimitReached(
        ILogger logger,
        ScmProvider provider,
        string repositoryId,
        int pageCap);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message =
            "{Provider} mention discovery found {Count} open pull requests across {RepositoryCount} claimed repositories in {ScopePath}")]
    internal static partial void Discovered(
        ILogger logger,
        ScmProvider provider,
        string scopePath,
        int repositoryCount,
        int count);
}
