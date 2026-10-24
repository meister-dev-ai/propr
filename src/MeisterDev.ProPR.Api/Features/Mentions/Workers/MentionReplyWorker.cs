// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Api.Workers;

/// <summary>
///     Background consumer worker that reads <see cref="MentionReplyJob" /> items from the
///     <see cref="Channel{T}" />, fetches PR context, generates an AI answer, and posts the
///     reply to the ADO thread. On startup, hydrates the channel from DB-persisted pending jobs.
///     Every minute, it returns stalled jobs to pending and offers the jobs that have waited too long again. An answer
///     that takes longer than <see cref="ProcessingTimeout" /> is stopped, and its job fails.
/// </summary>
public sealed partial class MentionReplyWorker(
    ChannelReader<MentionReplyJob> channelReader,
    ChannelWriter<MentionReplyJob> channelWriter,
    IServiceScopeFactory scopeFactory,
    ILogger<MentionReplyWorker> logger,
    TimeProvider? clock = null) : BackgroundService
{
    /// <summary>
    ///     How long a job may stay in processing before it counts as stalled, as for a thread pass. A job that waited
    ///     this long in pending is offered again.
    /// </summary>
    internal static readonly TimeSpan StalledAfter = TimeSpan.FromMinutes(15);

    /// <summary>The most waiting jobs that one recovery offers again.</summary>
    private const int MaxJobsPerRecovery = 50;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private TaskCompletionSource _startedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How often the worker recovers stalled and waiting jobs. One minute in the composition.</summary>
    internal TimeSpan RecoveryInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     How long the answer to one job may take. Ten minutes in the composition. The limit stays below
    ///     <see cref="StalledAfter" />, so the recovery cannot return a job to pending while this instance still answers
    ///     it, and a second instance cannot post another answer to the same mention.
    /// </summary>
    internal TimeSpan ProcessingTimeout { get; init; } = TimeSpan.FromMinutes(10);

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
        var recovery = this.RecoverPeriodicallyAsync(stoppingToken);

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

        await recovery;
        LogWorkerStopped(logger);
    }

    /// <summary>Recovers stalled and waiting jobs once per <see cref="RecoveryInterval" /> until the worker stops.</summary>
    /// <remarks>
    ///     The scan writes a new job into the channel of the instance that found it. When that instance stops, the job
    ///     stays pending, and when it stops while it answers, the job stays in processing. Another instance offers such
    ///     a job again. Only one process can claim a job, so a job that is offered twice is answered once.
    /// </remarks>
    private async Task RecoverPeriodicallyAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(this.RecoveryInterval, this._clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.RecoverAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetService<IMentionReplyJobRepository>();
            if (repo is null)
            {
                return;
            }

            var cutoff = this._clock.GetUtcNow() - StalledAfter;
            var reclaimed = await repo.ReclaimStalledAsync(cutoff, ct);
            if (reclaimed > 0)
            {
                LogStalledJobsReclaimed(logger, reclaimed);
            }

            foreach (var job in await repo.GetPendingAsync(cutoff, MaxJobsPerRecovery, ct))
            {
                await channelWriter.WriteAsync(job, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A cancellation that the shutdown of the worker did not cause is a failed recovery as well. The next
            // recovery runs as planned.
            LogRecoveryError(logger, ex);
        }
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

            // Another instance can still be answering a job in processing, so only a stalled job returns to pending.
            await repo.ReclaimStalledAsync(this._clock.GetUtcNow() - StalledAfter, cancellationToken);
            var pending = await repo.GetPendingAsync(cancellationToken);

            foreach (var job in pending)
            {
                await channelWriter.WriteAsync(job, cancellationToken);
            }

            LogHydrated(logger, pending.Count);

            await this.ReconcileReplyProvenanceAsync(scope, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogHydrationError(logger, ex);
        }
    }

    // The startup counterpart to reclaiming stalled jobs: a process that died between completing a mention job and
    // recording who posted its answer left the answer attributable to nobody, and this restart is the event that
    // follows. Its own try/catch because it is recovery bookkeeping: failing to rewrite an old provenance row
    // must never stop the pending jobs above from being worked.
    private async Task ReconcileReplyProvenanceAsync(AsyncServiceScope scope, CancellationToken cancellationToken)
    {
        var reconciler = scope.ServiceProvider.GetService<IMentionReplyProvenanceReconciler>();
        if (reconciler is null)
        {
            return;
        }

        try
        {
            await reconciler.ReconcileAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProvenanceReconciliationError(logger, ex);
        }
    }

    private async Task ProcessJobSafeAsync(MentionReplyJob job, CancellationToken stoppingToken)
    {
        using var deadline = new CancellationTokenSource(this.ProcessingTimeout, this._clock);
        using var processing = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var replyService = scope.ServiceProvider.GetService<IMentionReplyService>();
            if (replyService is null)
            {
                LogReplyServiceUnavailable(logger);
                return;
            }

            await replyService.ProcessAsync(job, processing.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            LogJobTimedOut(logger, job.Id, this.ProcessingTimeout.TotalMinutes);
            await this.FailTimedOutJobAsync(job);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJobError(logger, job.Id, ex);
        }
    }

    /// <summary>
    ///     Fails a job whose answer took longer than <see cref="ProcessingTimeout" />. A job that stayed in processing
    ///     would be answered again after the recovery, and an answer that always takes this long would then run without
    ///     end.
    /// </summary>
    /// <remarks>
    ///     The answer records its completion before the provenance and the author activity, so the deadline can end the
    ///     answer after the job is completed. Only a job that is still in processing therefore changes to failed. The
    ///     write does not observe the shutdown of the worker, like the drain of buffered jobs, so a shutdown that begins
    ///     after the deadline does not leave the job in processing.
    /// </remarks>
    private async Task FailTimedOutJobAsync(MentionReplyJob job)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetService<IMentionReplyJobRepository>();
            if (repo is not null
                && await repo.TryTransitionAsync(job.Id, MentionJobStatus.Processing, MentionJobStatus.Failed, CancellationToken.None))
            {
                await repo.SetFailedAsync(
                    job.Id,
                    $"The answer was stopped because it took longer than {this.ProcessingTimeout.TotalMinutes:0.#} minutes.",
                    CancellationToken.None);
            }
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
        Message = "MentionReplyWorker: rewriting missing mention-answer provenance failed")]
    private static partial void LogProvenanceReconciliationError(ILogger logger, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "MentionReplyWorker: failed to process job {JobId}")]
    private static partial void LogJobError(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MentionReplyWorker stopped the answer to job {JobId} after {Minutes} minutes and failed the job")]
    private static partial void LogJobTimedOut(ILogger logger, Guid jobId, double minutes);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "MentionReplyWorker returned {Count} stalled jobs to pending")]
    private static partial void LogStalledJobsReclaimed(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "MentionReplyWorker: recovering stalled and waiting jobs failed")]
    private static partial void LogRecoveryError(ILogger logger, Exception ex);
}
