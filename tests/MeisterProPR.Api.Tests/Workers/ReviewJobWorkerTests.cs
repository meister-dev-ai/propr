// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Workers;

public class ReviewJobWorkerTests
{
    private static ReviewJob CreateJob(int prId = 1)
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", prId, 1);
    }

    private static IOptions<WorkerOptions> DefaultWorkerOptions => Options.Create(new WorkerOptions());

    private static IServiceScopeFactory CreateScopeFactory(IJobRepository? repo = null)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);

        var effectiveRepo = repo ?? Substitute.For<IJobRepository>();
        effectiveRepo.GetPendingJobs().Returns(Array.Empty<ReviewJob>());
        serviceProvider.GetService(typeof(IJobRepository)).Returns(effectiveRepo);

        return scopeFactory;
    }

    [Fact]
    public async Task IsRunning_AfterStart_BecomesTrue()
    {
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(scopeFactory, DefaultWorkerOptions, logger);

        using var cts = new CancellationTokenSource();

        var workerTask = worker.StartAsync(cts.Token);
        await Task.Delay(100, cts.Token); // Give worker time to start

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
        var worker = new ReviewJobWorker(scopeFactory, DefaultWorkerOptions, logger);

        Assert.False(worker.IsRunning);
    }

    [Fact]
    public async Task Worker_ClaimsPendingJobAndTransitionsToProcessing()
    {
        var repo = Substitute.For<IJobRepository>();
        var job = CreateJob(101);
        repo.GetPendingJobs().Returns(new[] { job });
        repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing, Arg.Any<CancellationToken>())
            .Returns(true);

        var logger = Substitute.For<ILogger<ReviewJobWorker>>();

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);

        sp.GetService(typeof(IJobRepository)).Returns(repo);
        // Make the orchestration service return null — GetRequiredService will throw,
        // causing SetFailed to be called on the job.
        sp.GetService(typeof(ReviewOrchestrationService)).Returns(null);

        var worker = new ReviewJobWorker(scopeFactory, DefaultWorkerOptions, logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = worker.StartAsync(cts.Token);
        await Task.Delay(3000, CancellationToken.None); // Wait for worker to pick up job

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
        var worker = new ReviewJobWorker(scopeFactory, DefaultWorkerOptions, logger);

        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);
        await Task.Delay(200);

        Assert.True(worker.IsRunning);

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);

        // Give time for cleanup
        await Task.Delay(200);
        Assert.False(worker.IsRunning);
    }

    [Fact]
    public async Task Worker_UnhandledException_DoesNotCrashWorker()
    {
        var repo = Substitute.For<IJobRepository>();
        var job = CreateJob(777);
        repo.GetPendingJobs().Returns(new[] { job });
        repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing, Arg.Any<CancellationToken>())
            .Returns(true);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);

        sp.GetService(typeof(IJobRepository)).Returns(repo);
        // GetRequiredService throws — simulating unhandled exception in orchestration
        sp.GetService(typeof(ReviewOrchestrationService)).Returns(null);

        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(scopeFactory, DefaultWorkerOptions, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        _ = worker.StartAsync(cts.Token);

        await Task.Delay(3000, CancellationToken.None);

        // Worker should still be running despite the exception
        Assert.True(worker.IsRunning);

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }
}
