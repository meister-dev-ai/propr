// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Workers;

public class ReviewJobWorkerTests
{
    private static IOptions<WorkerOptions> CreateWorkerOptions(int pollIntervalMilliseconds = 25)
    {
        return Options.Create(new WorkerOptions { PollIntervalMilliseconds = pollIntervalMilliseconds });
    }

    private static ReviewJob CreateJob(int prId = 1)
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", prId, 1);
    }

    private static IServiceScopeFactory CreateScopeFactory(IReviewJobExecutionStore? repo = null)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);

        var effectiveRepo = repo ?? Substitute.For<IReviewJobExecutionStore>();
        effectiveRepo.GetPendingJobs().Returns(Array.Empty<ReviewJob>());
        effectiveRepo.GetStuckProcessingJobsAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([]));
        serviceProvider.GetService(typeof(IReviewJobExecutionStore)).Returns(effectiveRepo);
        serviceProvider.GetService(typeof(IReviewJobProcessor)).Returns(Substitute.For<IReviewJobProcessor>());

        return scopeFactory;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, CancellationToken.None);
        }

        Assert.True(condition(), failureMessage);
    }

    [Fact]
    public async Task IsRunning_AfterStart_BecomesTrue()
    {
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(scopeFactory, CreateWorkerOptions(), logger);

        using var cts = new CancellationTokenSource();

        var workerTask = worker.StartAsync(cts.Token);
        await WaitUntilAsync(() => worker.IsRunning, TimeSpan.FromSeconds(1), "Worker never entered the running state.");

        Assert.True(worker.IsRunning);

        await cts.CancelAsync();
        try
        {
            await workerTask;
        }
        catch
        {
        }

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void IsRunning_BeforeStart_IsFalse()
    {
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(scopeFactory, CreateWorkerOptions(), logger);

        Assert.False(worker.IsRunning);
    }

    [Fact]
    public async Task Worker_ClaimsPendingJobAndTransitionsToProcessing()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var job = CreateJob(101);
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repo.GetPendingJobs().Returns(new[] { job });
        repo.GetStuckProcessingJobsAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([]));
        repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                claimed.TrySetResult();
                return true;
            });

        var logger = Substitute.For<ILogger<ReviewJobWorker>>();

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);

        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        // Make the Reviewing processor return null — GetRequiredService will throw,
        // causing SetFailed to be called on the job.
        sp.GetService(typeof(IReviewJobProcessor)).Returns(null);

        var worker = new ReviewJobWorker(scopeFactory, CreateWorkerOptions(), logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = worker.StartAsync(cts.Token);
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);

        // Job should have been picked up — TryTransition to Processing was called
        await repo.Received().TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_IsRunning_BecomesFalseAfterStop()
    {
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(scopeFactory, CreateWorkerOptions(), logger);

        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);
        await WaitUntilAsync(() => worker.IsRunning, TimeSpan.FromSeconds(1), "Worker never entered the running state.");

        Assert.True(worker.IsRunning);

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
        Assert.False(worker.IsRunning);
    }

    [Fact]
    public async Task Worker_UnhandledException_DoesNotCrashWorker()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var job = CreateJob(777);
        var jobFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repo.GetPendingJobs().Returns(new[] { job });
        repo.GetStuckProcessingJobsAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([]));
        repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing, Arg.Any<CancellationToken>())
            .Returns(true);
        repo.SetFailedAsync(job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                jobFailed.TrySetResult();
                return Task.CompletedTask;
            });

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);

        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        // GetRequiredService throws — simulating unhandled exception in the Reviewing processor.
        sp.GetService(typeof(IReviewJobProcessor)).Returns(null);

        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(scopeFactory, CreateWorkerOptions(), logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        _ = worker.StartAsync(cts.Token);

        await jobFailed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        // Worker should still be running despite the exception
        Assert.True(worker.IsRunning);

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_WhenProcessorMissing_MarksClaimedJobFailed()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var job = CreateJob(909);
        var jobFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repo.GetPendingJobs().Returns(new[] { job });
        repo.GetStuckProcessingJobsAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReviewJob>>([]));
        repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing, Arg.Any<CancellationToken>())
            .Returns(true);
        repo.SetFailedAsync(job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                jobFailed.TrySetResult();
                return Task.CompletedTask;
            });

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);

        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        sp.GetService(typeof(IReviewJobProcessor)).Returns((object?)null);

        var worker = new ReviewJobWorker(scopeFactory, CreateWorkerOptions(), Substitute.For<ILogger<ReviewJobWorker>>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        _ = worker.StartAsync(cts.Token);
        await jobFailed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await repo.Received().SetFailedAsync(job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }
}
