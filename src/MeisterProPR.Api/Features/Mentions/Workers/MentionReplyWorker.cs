// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Api.Workers;

/// <summary>
///     Background consumer worker that reads <see cref="MentionReplyJob" /> items from the
///     <see cref="Channel{T}" />, fetches PR context, generates an AI answer, and posts the
///     reply to the ADO thread. On startup, hydrates the channel from DB-persisted pending jobs.
/// </summary>
public sealed partial class MentionReplyWorker(
    ChannelReader<MentionReplyJob> channelReader,
    ChannelWriter<MentionReplyJob> channelWriter,
    IServiceScopeFactory scopeFactory,
    ILogger<MentionReplyWorker> logger) : BackgroundService
{
    private TaskCompletionSource _startedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        this._startedSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await this.HydratePendingJobsAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
        await this._startedSignal.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await this.DrainBufferedJobsAsync();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        this._startedSignal.TrySetResult();
        LogWorkerStarted(logger);

        try
        {
            await foreach (var job in channelReader.ReadAllAsync(stoppingToken))
            {
                await this.ProcessJobSafeAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            await this.DrainBufferedJobsAsync();
        }

        LogWorkerStopped(logger);
    }

    private async Task HydratePendingJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetService<IMentionReplyJobRepository>();
            if (repo is null)
            {
                return;
            }

            await repo.ResetStuckProcessingAsync(cancellationToken);
            var pending = await repo.GetPendingAsync(cancellationToken);

            foreach (var job in pending)
            {
                await channelWriter.WriteAsync(job, cancellationToken);
            }

            LogHydrated(logger, pending.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogHydrationError(logger, ex);
        }
    }

    private async Task ProcessJobSafeAsync(MentionReplyJob job, CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var replyService = scope.ServiceProvider.GetService<IMentionReplyService>();
            if (replyService is null)
            {
                LogReplyServiceUnavailable(logger);
                return;
            }

            await replyService.ProcessAsync(job, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJobError(logger, job.Id, ex);
        }
    }

    private async Task DrainBufferedJobsAsync()
    {
        while (channelReader.TryRead(out var job))
        {
            await this.ProcessJobSafeAsync(job, CancellationToken.None);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MentionReplyWorker started")]
    private static partial void LogWorkerStarted(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MentionReplyWorker stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "MentionReplyWorker hydrated {Count} pending jobs from DB")]
    private static partial void LogHydrated(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "MentionReplyWorker: hydration failed")]
    private static partial void LogHydrationError(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "MentionReplyWorker: IMentionReplyService not registered — job skipped")]
    private static partial void LogReplyServiceUnavailable(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "MentionReplyWorker: failed to process job {JobId}")]
    private static partial void LogJobError(ILogger logger, Guid jobId, Exception ex);
}
