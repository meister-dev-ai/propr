// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Api.Telemetry;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Observability;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>Processes durable discussion signals and due full reconciliations independently of review discovery.</summary>
public sealed class DiscussionRefreshWorker(
    IServiceScopeFactory scopes,
    ICrawlWorkScheduler scheduler,
    IOptions<DiscussionSynchronizationOptions> options,
    ILogger<DiscussionRefreshWorker> logger,
    ReviewJobMetrics metrics) : BackgroundService
{
    private static readonly TimeSpan DueWorkPollInterval = TimeSpan.FromSeconds(1);
    private readonly Dictionary<Guid, (DiscussionSynchronizationState State, Task Work)> _running = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(DueWorkPollInterval);
        try
        {
            do
            {
                try
                {
                    await this.RunCycleAsync(stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Discussion refresh scheduling failed; pending work remains durable. {ExceptionType}, code {ErrorCode}.", exception.GetType().Name,
                        exception.HResult);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await this.DrainAsync();
        }
    }

    internal async Task RunCycleAsync(CancellationToken ct)
    {
        await this.ObserveCompletedAsync();
        if (this._running.Count >= Math.Max(1, options.Value.GlobalConcurrency))
        {
            return;
        }

        using var background = BackgroundActivityScope.Begin();
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetService<IDiscussionSynchronizationStore>();
        if (store is null)
        {
            return;
        }

        var due = await store.ListDueAsync(options.Value.ReconciliationInterval, 128, ct);
        foreach (var state in due)
        {
            if (this._running.Count >= Math.Max(1, options.Value.GlobalConcurrency))
            {
                break;
            }

            if (this._running.ContainsKey(state.Id))
            {
                continue;
            }

            var host = HostKey(state);
            if (this._running.Values.Count(item => HostKey(item.State) == host) >= Math.Max(1, options.Value.ProviderHostConcurrency) ||
                this._running.Values.Count(item => item.State.ClientId == state.ClientId && HostKey(item.State) == host) >=
                Math.Max(1, options.Value.ClientProviderHostConcurrency))
            {
                continue;
            }

            this._running.Add(
                state.Id, (state, scheduler.RunAsync(
                    [state],
                    item => new CrawlWorkScope(item.ClientId, item.Provider, item.ProviderScopePath), this.RefreshAsync, ct)));
        }
    }

    internal async Task DrainAsync()
    {
        foreach (var (_, work) in this._running.Values.ToArray())
        {
            await this.ObserveAsync(work);
        }

        this._running.Clear();
    }

    private async Task ObserveCompletedAsync()
    {
        foreach (var id in this._running.Where(item => item.Value.Work.IsCompleted).Select(item => item.Key).ToArray())
        {
            await this.ObserveAsync(this._running[id].Work);
            this._running.Remove(id);
        }
    }

    private async Task ObserveAsync(Task work)
    {
        try
        {
            await work;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Discussion dispatch failed; pending work remains durable. {ExceptionType}, code {ErrorCode}.", exception.GetType().Name, exception.HResult);
        }
    }

    private static string HostKey(DiscussionSynchronizationState state) => state.Provider + ":" +
                                                                           (Uri.TryCreate(state.ProviderScopePath, UriKind.Absolute, out var uri)
                                                                               ? uri.Authority.ToLowerInvariant()
                                                                               : state.ProviderScopePath.ToLowerInvariant());

    private static PullRequestThreadSnapshotTarget Target(DiscussionSynchronizationState state) => new(
        state.ClientId, state.ConnectionId, state.Provider,
        state.ProviderScopePath, state.ProviderProjectKey, state.RepositoryId, state.PullRequestId);

    private static async Task DeferForRetryAsync(DiscussionSynchronizationState state, IDiscussionSynchronizationStore store, CancellationToken ct)
    {
        var current = await store.ReadAsync(Target(state), ct);
        await store.DeferAsync(state.Id, current?.RequestedGeneration ?? state.RequestedGeneration, TimeSpan.FromSeconds(15), ct);
    }

    /// <summary>
    ///     Counts a refresh that could not read the discussion. An attempt that failed under a lease was already counted
    ///     and deferred by the lease; one that failed before it, such as one whose connection no longer matches the pull
    ///     request, left the state due, and without this it would be picked up again on the next poll a second later.
    ///     The generation is the one this attempt worked on, so a request that arrived during the attempt is still
    ///     processed on the next poll.
    /// </summary>
    private static Task DeferUnavailableDiscussionAsync(DiscussionSynchronizationState state, IDiscussionSynchronizationStore store, CancellationToken ct) =>
        store.DeferAfterFailureAsync(state.Id, state.RequestedGeneration, state.ConsecutiveFailures, ct);

    /// <summary>
    ///     Counts a refresh that threw and reopens its state for the current generation, so that work which the failed
    ///     refresh left unfinished after a completed observation, such as lifecycle completion, is retried.
    /// </summary>
    private static async Task DeferAfterFailureAsync(DiscussionSynchronizationState state, IDiscussionSynchronizationStore store, CancellationToken ct)
    {
        var current = await store.ReadAsync(Target(state), ct);
        await store.DeferAfterFailureAsync(state.Id, current?.RequestedGeneration ?? state.RequestedGeneration, state.ConsecutiveFailures, ct);
    }

    private async Task RefreshAsync(DiscussionSynchronizationState state, CancellationToken ct)
    {
        metrics.RecordDiscussionDelay(
            state.Provider, state.NextAttemptAt == DateTimeOffset.MinValue ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - state.NextAttemptAt).TotalSeconds));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, options.Value.OperationTimeoutSeconds)));
        await using var scope = scopes.CreateAsyncScope();
        try
        {
            var connections = scope.ServiceProvider.GetRequiredService<IClientScmConnectionRepository>();
            var connection = await connections.GetByIdAsync(state.ClientId, state.ConnectionId, deadline.Token);
            if (connection is null || !connection.IsActive || connection.ProviderFamily != state.Provider)
            {
                await scope.ServiceProvider.GetRequiredService<IDiscussionSynchronizationStore>().DeferAsync(
                    state.Id, state.RequestedGeneration,
                    options.Value.ReconciliationInterval, deadline.Token);
                return;
            }

            var activation = scope.ServiceProvider.GetService<IProviderActivationService>();
            if (activation is not null && !await activation.IsEnabledAsync(state.Provider, deadline.Token))
            {
                await scope.ServiceProvider.GetRequiredService<IDiscussionSynchronizationStore>().DeferAsync(
                    state.Id, state.RequestedGeneration,
                    options.Value.ReconciliationInterval, deadline.Token);
                return;
            }

            var registry = scope.ServiceProvider.GetRequiredService<IScmProviderRegistry>();
            var repository = registry.GetReviewSourcePolicy(state.Provider).CreateCapturedRepository(
                state.ProviderScopePath,
                state.RepositoryId, state.ProviderProjectKey, state.RepositoryId);
            var review = new CodeReviewRef(
                repository, CodeReviewPlatformKind.PullRequest,
                state.PullRequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), state.PullRequestId);
            var outcome = await scope.ServiceProvider.GetRequiredService<IPullRequestSynchronizationService>().SynchronizeAsync(
                new PullRequestSynchronizationRequest
                {
                    ActivationSource = PullRequestActivationSource.Crawl, SummaryLabel = "discussion refresh", ClientId = state.ClientId,
                    ProviderScopePath = state.ProviderScopePath, ProviderProjectKey = state.ProviderProjectKey, RepositoryId = state.RepositoryId,
                    PullRequestId = state.PullRequestId, PullRequestStatus = PrStatus.Active, Provider = state.Provider, Host = repository.Host,
                    ConnectionId = state.ConnectionId,
                    Repository = repository, CodeReview = review,
                    AllowReviewSubmission = false, DiscussionOnly = true,
                }, deadline.Token);
            if (outcome.DiscussionUnavailable)
            {
                await DeferUnavailableDiscussionAsync(state, scope.ServiceProvider.GetRequiredService<IDiscussionSynchronizationStore>(), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Closure can be persisted before lifecycle cancellation finishes. Keep that remaining work due after shutdown.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await DeferForRetryAsync(state, scope.ServiceProvider.GetRequiredService<IDiscussionSynchronizationStore>(), cleanup.Token);
            throw;
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && deadline.IsCancellationRequested)
            {
                logger.LogWarning("Discussion refresh timed out for provider {Provider} PR {PullRequestId}.", state.Provider, state.PullRequestId);
            }
            else
            {
                logger.LogWarning(
                    "Discussion refresh failed for provider {Provider} PR {PullRequestId}: {ExceptionType}, code {ErrorCode}.", state.Provider,
                    state.PullRequestId,
                    exception.GetType().Name, exception.HResult);
            }

            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await DeferAfterFailureAsync(state, scope.ServiceProvider.GetRequiredService<IDiscussionSynchronizationStore>(), cleanup.Token);
        }
    }
}
