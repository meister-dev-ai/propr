using MeisterProPR.Api.Workers;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Workers;

public class ReviewJobWorkerTests
{
    private static ReviewJob CreateJob(int prId = 1)
    {
        return new ReviewJob(Guid.NewGuid(), "test-client", "https://dev.azure.com/org", "proj", "repo", prId, 1);
    }

    private static IServiceScopeFactory CreateScopeFactory(Action<IServiceProvider>? configureServices = null)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);

        return scopeFactory;
    }

    [Fact]
    public async Task IsRunning_AfterStart_BecomesTrue()
    {
        var repo = new InMemoryJobRepository();
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(repo, scopeFactory, logger);

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
        var repo = new InMemoryJobRepository();
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(repo, scopeFactory, logger);

        Assert.False(worker.IsRunning);
    }

    [Fact]
    public async Task Worker_ClaimsPendingJobAndTransitionsToProcessing()
    {
        var repo = new InMemoryJobRepository();
        var job = CreateJob(101);
        repo.Add(job);

        // We'll use the real InMemoryJobRepository and just observe status changes
        var logger = Substitute.For<ILogger<ReviewJobWorker>>();

        // Create a scope factory that uses a service that just transitions the job
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);

        // Make the orchestration service just signal and wait
        sp.GetService(typeof(ReviewOrchestrationService))
            .Returns(null); // Will throw - that's ok, SetFailed will be called

        var worker = new ReviewJobWorker(repo, scopeFactory, logger);
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
        var repo = new InMemoryJobRepository();
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(repo, scopeFactory, logger);

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
        // GetRequiredService throws - simulating unhandled exception
        sp.GetService(typeof(ReviewOrchestrationService))
            .Returns(null);

        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(repo, scopeFactory, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        _ = worker.StartAsync(cts.Token);

        await Task.Delay(3000, CancellationToken.None);

        // Worker should still be running despite the exception
        Assert.True(worker.IsRunning);

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }
}