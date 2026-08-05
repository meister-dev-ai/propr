// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterDev.ProPR.Application.Features.Reviewing.Threads.Ports;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>
///     Background consumer that runs the thread passes <see cref="ThreadPassScanWorker" /> hands it: resolves
///     the reviewer-owned threads a developer has fixed and answers the ones they replied to.
/// </summary>
/// <remarks>
///     No boot-time rehydration of its own. Every pass is a durable row that the scan worker re-offers on its
///     next tick, and the pass confirms the pull request is still active before it acts, so a queue left over
///     from a previous process drains instead of becoming permanent.
/// </remarks>
public sealed partial class ThreadPassWorker(
    ChannelReader<ThreadPassJob> channelReader,
    IServiceScopeFactory scopeFactory,
    ILogger<ThreadPassWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(logger);

        try
        {
            await foreach (var job in channelReader.ReadAllAsync(stoppingToken))
            {
                await this.ProcessPassSafeAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        LogWorkerStopped(logger);
    }

    private async Task ProcessPassSafeAsync(ThreadPassJob job, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var threadPassService = scope.ServiceProvider.GetService<IThreadPassService>();
            if (threadPassService is null)
            {
                LogServiceUnavailable(logger);
                return;
            }

            await threadPassService.ProcessAsync(job, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPassError(logger, job.Id, ex);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ThreadPassWorker started")]
    private static partial void LogWorkerStarted(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ThreadPassWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "ThreadPassWorker: IThreadPassService not registered, so the thread pass was skipped")]
    private static partial void LogServiceUnavailable(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "ThreadPassWorker: failed to run thread pass {ThreadPassJobId}")]
    private static partial void LogPassError(ILogger logger, Guid threadPassJobId, Exception ex);
}
