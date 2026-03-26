using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Repositories;
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

        var effectiveRepo = repo ?? new InMemoryJobRepository();
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
        var repo = new InMemoryJobRepository();
        var job = CreateJob(101);
        repo.Add(job);

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

        // Job should have been picked up and either Failed (due to null service) or is no longer Pending
        var retrieved = repo.GetById(job.Id);
        Assert.NotNull(retrieved);
        Assert.NotEqual(JobStatus.Pending, retrieved!.Status);
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
        var repo = new InMemoryJobRepository();
        var job = CreateJob(777);
        repo.Add(job);

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
