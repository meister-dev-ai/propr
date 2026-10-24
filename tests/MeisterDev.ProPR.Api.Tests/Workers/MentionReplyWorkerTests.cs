// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterDev.ProPR.Api.Workers;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Api.Tests.Workers;

public sealed class MentionReplyWorkerTests
{
    [Fact]
    public async Task StartAsync_WhenRepositoryMissing_DoesNotThrow()
    {
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns((IServiceScope)scope);
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns((object?)null);

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance);

        var ex = await Record.ExceptionAsync(() => worker.StartAsync(CancellationToken.None));

        Assert.Null(ex);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenReplyServiceMissing_SkipsJobWithoutThrowing()
    {
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var processingAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scopeCreationCount = 0;

        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope()
            .Returns(_ =>
            {
                scopeCreationCount++;
                if (scopeCreationCount >= 2)
                {
                    processingAttempted.TrySetResult();
                }

                return (IServiceScope)scope;
            });

        var repo = Substitute.For<IMentionReplyJobRepository>();
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([]);
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns((object?)null);

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        var ex = await Record.ExceptionAsync(async () =>
        {
            await channel.Writer.WriteAsync(
                new MentionReplyJob(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    "https://dev.azure.com/org",
                    "proj",
                    "repo",
                    7,
                    "3",
                    11,
                    "@bot please help"));
            await processingAttempted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        });

        Assert.Null(ex);
        Assert.True(scopeCreationCount >= 2, "the worker should have continued to the next polling cycle after skipping the job");
        await worker.StopAsync(CancellationToken.None);
    }

    // An instance that stops while it answers a job leaves the job in processing, and one that stops before leaves it
    // in pending. Another instance returns stalled jobs to pending and offers the jobs that have waited too long again.
    [Fact]
    public async Task ExecuteAsync_RecoversStalledAndWaitingJobsPeriodically()
    {
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns((IServiceScope)scope);
        var waiting = new MentionReplyJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "proj",
            "repo",
            7,
            "3",
            11,
            "@bot please help");
        var repo = Substitute.For<IMentionReplyJobRepository>();
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([]);
        repo.GetPendingAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([waiting], new List<MentionReplyJob>());
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replyService = Substitute.For<IMentionReplyService>();
        replyService.ProcessAsync(waiting, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                processed.TrySetResult();
                return Task.CompletedTask;
            });
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns(replyService);

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance)
        {
            RecoveryInterval = TimeSpan.FromMilliseconds(20),
        };

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // At startup and in every recovery, a job counts as stalled only after it has been in processing for the stall
        // time, because another instance can still be answering a job that started later.
        var cutoffs = repo.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IMentionReplyJobRepository.ReclaimStalledAsync))
            .Select(call => (DateTimeOffset)call.GetArguments()[0]!)
            .ToList();
        Assert.True(cutoffs.Count >= 2);
        Assert.All(cutoffs, cutoff => Assert.True(cutoff <= DateTimeOffset.UtcNow - MentionReplyWorker.StalledAfter));
    }

    // An answer that takes longer than the processing timeout is stopped and its job fails. The recovery therefore
    // never returns a job to pending while this instance still answers it, and the worker goes on with the next job.
    [Fact]
    public async Task ExecuteAsync_StopsAnAnswerThatTakesLongerThanTheTimeout_AndFailsItsJob()
    {
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns((IServiceScope)scope);
        var slow = CreateJob();
        var next = CreateJob();
        var repo = Substitute.For<IMentionReplyJobRepository>();
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([]);
        repo.TryTransitionAsync(slow.Id, MentionJobStatus.Processing, MentionJobStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(true);
        var nextProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replyService = Substitute.For<IMentionReplyService>();
        replyService.ProcessAsync(slow, Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()));
        replyService.ProcessAsync(next, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                nextProcessed.TrySetResult();
                return Task.CompletedTask;
            });
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns(replyService);

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance)
        {
            ProcessingTimeout = TimeSpan.FromMilliseconds(50),
        };

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await channel.Writer.WriteAsync(slow);
            await channel.Writer.WriteAsync(next);
            await nextProcessed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await repo.Received(1).SetFailedAsync(slow.Id, Arg.Is<string>(message => message.Contains("took longer")), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().SetFailedAsync(next.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The timeout of the composition ends an answer before the recovery counts its job as stalled.
        var composed = new MentionReplyWorker(channel.Reader, channel.Writer, scopeFactory, NullLogger<MentionReplyWorker>.Instance);
        Assert.True(composed.ProcessingTimeout < MentionReplyWorker.StalledAfter);
    }

    // The answer records its completion before its provenance and author activity. A deadline that ends the answer
    // after the completion leaves the job completed.
    [Fact]
    public async Task ExecuteAsync_WhenTheTimeoutEndsAnAnswerAfterItsCompletion_KeepsTheJobCompleted()
    {
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns((IServiceScope)scope);
        var completed = CreateJob();
        var repo = Substitute.For<IMentionReplyJobRepository>();
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([]);

        // The stored job is completed, so the transition from processing to failed does not apply.
        repo.TryTransitionAsync(completed.Id, MentionJobStatus.Processing, MentionJobStatus.Failed, Arg.Any<CancellationToken>())
            .Returns(false);
        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replyService = Substitute.For<IMentionReplyService>();
        replyService.ProcessAsync(completed, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                }
                finally
                {
                    timedOut.TrySetResult();
                }
            });
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns(replyService);

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance)
        {
            ProcessingTimeout = TimeSpan.FromMilliseconds(50),
        };

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await channel.Writer.WriteAsync(completed);
            await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForCallAsync(() => repo.ReceivedCalls().Any(call =>
                call.GetMethodInfo().Name == nameof(IMentionReplyJobRepository.TryTransitionAsync)));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await repo.DidNotReceive().SetFailedAsync(completed.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A cancellation that the shutdown of the worker did not cause ends one recovery, and the next recovery runs.
    [Fact]
    public async Task ExecuteAsync_WhenARecoveryIsCancelledWithoutShutdown_KeepsRecovering()
    {
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns((IServiceScope)scope);
        var repo = Substitute.For<IMentionReplyJobRepository>();
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([]);
        repo.GetPendingAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        var recoveries = 0;
        var recoveredAfterCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repo.ReclaimStalledAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // The first call comes from the startup and the second from the first recovery.
                var call = Interlocked.Increment(ref recoveries);
                if (call == 2)
                {
                    throw new OperationCanceledException("A database command was cancelled.");
                }

                if (call >= 3)
                {
                    recoveredAfterCancellation.TrySetResult();
                }

                return Task.FromResult(0);
            });
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns((object?)null);

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance)
        {
            RecoveryInterval = TimeSpan.FromMilliseconds(20),
        };

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await recoveredAfterCancellation.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_RewritesProvenanceAnEarlierRunLost()
    {
        // A process that died between completing a mention job and recording who posted its answer left the
        // answer attributable to nobody, and this startup is the event that follows it. The same moment already
        // recovers jobs that were in flight, so it is where the lost bookkeeping is recovered too.
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns((IServiceScope)scope);

        var repo = Substitute.For<IMentionReplyJobRepository>();
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([]);
        var reconciler = Substitute.For<IMentionReplyProvenanceReconciler>();
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyProvenanceReconciler)).Returns(reconciler);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns((object?)null);

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        await reconciler.Received(1).ReconcileAsync(Arg.Any<CancellationToken>());
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WhenRewritingProvenanceFails_StillHydratesThePendingJobs()
    {
        // Recovery bookkeeping runs after hydration and cannot undo it: an old provenance row that could not be
        // rewritten must not cost the worker the jobs it is meant to be working now.
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IAsyncDisposable, IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        ((IServiceScope)scope).ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateScope().Returns((IServiceScope)scope);

        var pending = new MentionReplyJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "proj",
            "repo",
            7,
            "3",
            11,
            "@bot please help");

        var repo = Substitute.For<IMentionReplyJobRepository>();
        repo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns([pending]);
        var reconciler = Substitute.For<IMentionReplyProvenanceReconciler>();
        reconciler.ReconcileAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("provenance store unavailable"));
        serviceProvider.GetService(typeof(IMentionReplyJobRepository)).Returns(repo);
        serviceProvider.GetService(typeof(IMentionReplyProvenanceReconciler)).Returns(reconciler);
        serviceProvider.GetService(typeof(IMentionReplyService)).Returns((object?)null);

        var worker = new MentionReplyWorker(
            channel.Reader,
            channel.Writer,
            scopeFactory,
            NullLogger<MentionReplyWorker>.Instance);

        var ex = await Record.ExceptionAsync(() => worker.StartAsync(CancellationToken.None));

        Assert.Null(ex);
        await repo.Received(1).GetPendingAsync(Arg.Any<CancellationToken>());
        await worker.StopAsync(CancellationToken.None);
    }

    private static async Task WaitForCallAsync(Func<bool> received)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!received())
        {
            Assert.True(DateTime.UtcNow < deadline, "The expected repository call did not happen.");
            await Task.Delay(10);
        }
    }

    private static MentionReplyJob CreateJob()
    {
        return new MentionReplyJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "proj",
            "repo",
            7,
            "3",
            11,
            "@bot please help");
    }
}
