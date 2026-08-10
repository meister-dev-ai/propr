// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Commands.HandleProviderWebhookDelivery;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>
///     Turns accepted webhook deliveries into reviews, on its own time.
///     <para>
///         The work this does — reading a pull request from the provider, resolving its revision, queueing
///         the review — takes seconds, and used to happen inside the provider's own HTTP request. Every
///         provider gives up after a few seconds, so a burst of deliveries dropped exactly the ones that
///         arrived together, and nothing recorded the loss. Here there is no caller waiting, so a slow
///         provider costs latency instead of a review.
///     </para>
///     <para>
///         Several deliveries at once, up to a configured bound. This began as strictly one at a time, on
///         the reasoning that the reviews it queues have their own concurrency; measured against a runner
///         fleet, that turned out to make intake the limit — roughly one job created every four seconds
///         while six execution slots stood idle. The bound remains because each delivery reads a pull
///         request from the provider that sent it, and the provider's rate limit is the real ceiling.
///     </para>
///     <para>
///         Concurrency is several copies of the same loop rather than a batch: the claim is one atomic
///         statement with <c>FOR UPDATE SKIP LOCKED</c>, so two loops cannot take the same delivery for
///         exactly the reason two replicas cannot. Nothing about the drain had to change to run it twice.
///     </para>
/// </summary>
public sealed partial class WebhookDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WebhookDeliveryWorkerOptions> options,
    TimeProvider timeProvider,
    ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    /// <summary>Identity stamped on the claims this process holds, so a stalled replica can be recognised.</summary>
    private static readonly string ClaimOwner = $"{Environment.MachineName}:{Environment.ProcessId}";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        LogWorkerStarted(logger, settings.MaxAttempts, settings.MaxConcurrency);

        // Each loop is independent and claims for itself. Awaited together so the worker stops when they
        // all do; one loop failing to the point of exiting would otherwise leave the rest looking healthy.
        await Task.WhenAll(
            Enumerable
                .Range(0, settings.MaxConcurrency)
                .Select(slot => this.DrainLoopAsync(settings, slot, stoppingToken)));
    }

    private async Task DrainLoopAsync(WebhookDeliveryWorkerOptions settings, int slot, CancellationToken stoppingToken)
    {
        // The slot rides on the claim owner so a delivery stuck in one loop can be told from a replica
        // that stopped entirely.
        var owner = $"{ClaimOwner}#{slot}";

        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;

            try
            {
                // One sweeper, not one per loop. Returning stranded claims is idempotent, so N loops
                // doing it would be correct and simply wasteful.
                worked = await this.DrainOneAsync(settings, owner, releaseExpired: slot == 0, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // A queue worker that dies on one bad delivery stops every other one.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogCycleFailed(logger, ex);
            }

            if (worked)
            {
                // Straight on to the next one: a backlog is exactly when waiting is wrong.
                continue;
            }

            try
            {
                await Task.Delay(settings.IdlePollInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> DrainOneAsync(
        WebhookDeliveryWorkerOptions settings,
        string owner,
        bool releaseExpired,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IWebhookDeliveryQueue>();

        if (releaseExpired)
        {
            // Claims whose holder stopped reporting come back first, so a replica that died mid-delivery
            // does not strand the review behind it.
            await queue.ReleaseExpiredClaimsAsync(ct);
        }

        var item = await queue.ClaimNextAsync(owner, settings.ClaimDuration, ct);
        if (item is null)
        {
            return false;
        }

        var handler = scope.ServiceProvider.GetRequiredService<HandleProviderWebhookDeliveryHandler>();

        try
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(item.HeadersJson)
                          ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var decision = await handler.HandleAsync(
                new HandleProviderWebhookDeliveryCommand(
                    item.Provider,
                    item.PathKey,
                    headers,
                    item.Payload,
                    WebhookDeliveryProcessingMode.Process),
                ct);

            await queue.CompleteAsync(item.Id, ct);
            LogDeliveryProcessed(logger, item.Id, item.Provider, decision.DeliveryOutcome.ToString(), item.Attempts);
        }
#pragma warning disable CA1031 // Any failure is the delivery's, and the delivery is retried rather than lost.
        catch (Exception ex) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            await queue.FailAsync(item.Id, ex.Message, settings.MaxAttempts, settings.RetryBackoff, ct);
            LogDeliveryFailed(logger, item.Id, item.Provider, item.Attempts, settings.MaxAttempts, ex);
        }

        return true;
    }

    [LoggerMessage(
        EventId = 2840,
        Level = LogLevel.Information,
        Message =
            "Webhook delivery worker started, working up to {MaxConcurrency} deliveries at once; a delivery is retried up to {MaxAttempts} times before it is kept as failed.")]
    private static partial void LogWorkerStarted(ILogger logger, int maxAttempts, int maxConcurrency);

    [LoggerMessage(
        EventId = 2841,
        Level = LogLevel.Information,
        Message = "Processed queued webhook delivery {DeliveryId} for {Provider}: {Outcome} (attempt {Attempt}).")]
    private static partial void LogDeliveryProcessed(
        ILogger logger,
        Guid deliveryId,
        Domain.Enums.ScmProvider provider,
        string outcome,
        int attempt);

    [LoggerMessage(
        EventId = 2842,
        Level = LogLevel.Warning,
        Message = "Queued webhook delivery {DeliveryId} for {Provider} failed on attempt {Attempt} of {MaxAttempts}.")]
    private static partial void LogDeliveryFailed(
        ILogger logger,
        Guid deliveryId,
        Domain.Enums.ScmProvider provider,
        int attempt,
        int maxAttempts,
        Exception ex);

    [LoggerMessage(EventId = 2843, Level = LogLevel.Warning, Message = "A webhook delivery worker cycle failed and will be retried.")]
    private static partial void LogCycleFailed(ILogger logger, Exception ex);
}
