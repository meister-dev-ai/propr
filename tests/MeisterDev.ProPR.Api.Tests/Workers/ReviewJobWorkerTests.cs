// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.ProPR.Api.Telemetry;
using MeisterDev.ProPR.Api.Workers;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Workers;

public class ReviewJobWorkerTests
{
    private static IOptions<WorkerOptions> CreateWorkerOptions(
        int pollIntervalMilliseconds = 25,
        int maxConcurrentReviewJobs = 4,
        int? retiredStuckJobTimeoutMinutes = null)
    {
        return Options.Create(
            new WorkerOptions
            {
                PollIntervalMilliseconds = pollIntervalMilliseconds,
                MaxConcurrentReviewJobs = maxConcurrentReviewJobs,
                RetiredStuckJobTimeoutMinutes = retiredStuckJobTimeoutMinutes,
            });
    }

    private static IOptions<ReviewLeaseOptions> CreateLeaseOptions()
    {
        return Options.Create(new ReviewLeaseOptions());
    }

    private static ReviewJob CreateJob(int prId = 1)
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", prId, 1);
    }

    /// <summary>
    ///     A lease store that offers the supplied jobs as claim candidates and grants each exactly once,
    ///     which is what the database does: a second claim of the same job finds it no longer pending.
    /// </summary>
    private static IReviewJobLeaseStore CreateLeaseStore(params ReviewJob[] candidates)
    {
        var store = Substitute.For<IReviewJobLeaseStore>();
        var granted = new HashSet<Guid>();

        store.GetClaimCandidatesAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<ReviewJob>>(call.ArgAt<DateTimeOffset?>(1) is null ? candidates : []));

        store.TryClaimAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var jobId = call.ArgAt<Guid>(0);
                lock (granted)
                {
                    if (!granted.Add(jobId))
                    {
                        return Task.FromResult<ReviewJobLease?>(null);
                    }
                }

                return Task.FromResult<ReviewJobLease?>(new ReviewJobLease(jobId, call.ArgAt<string>(1), 1, DateTimeOffset.UtcNow.AddMinutes(2)));
            });

        store.TryRenewAsync(Arg.Any<ReviewJobLease>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewJobLeaseRenewal(true, DateTimeOffset.UtcNow.AddMinutes(2)));

        return store;
    }

    private static ReviewJobMetrics CreateMetrics()
    {
        return new ReviewJobMetrics(Substitute.For<IServiceScopeFactory>());
    }

    private static IReviewJobCancellationRegistry CreateCancellationRegistry()
    {
        return new ReviewJobCancellationRegistry();
    }

    private static IServiceScopeFactory CreateScopeFactory(IReviewJobExecutionStore? repo = null)
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);

        var effectiveRepo = repo ?? Substitute.For<IReviewJobExecutionStore>();
        var leaseStore = CreateLeaseStore();
        serviceProvider.GetService(typeof(IReviewJobExecutionStore)).Returns(effectiveRepo);
        serviceProvider.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
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
        var worker = new ReviewJobWorker(
            scopeFactory, CreateWorkerOptions(), CreateLeaseOptions(),
            CreateMetrics(), CreateCancellationRegistry(),
            TimeProvider.System, logger);

        using var cts = new CancellationTokenSource();

        var workerTask = worker.StartAsync(cts.Token);
        await WaitUntilAsync(
            () => worker.IsRunning,
            TimeSpan.FromSeconds(1),
            "Worker never entered the running state.");

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
        var worker = new ReviewJobWorker(
            scopeFactory, CreateWorkerOptions(), CreateLeaseOptions(),
            CreateMetrics(), CreateCancellationRegistry(),
            TimeProvider.System, logger);

        Assert.False(worker.IsRunning);
    }

    [Fact]
    public async Task Worker_HoldsPendingJob_WhenABudgetCapIsAlreadyReached()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var job = CreateJob(202);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseStore = CreateLeaseStore(job);
        repo.SetBudgetHeldAsync(
                job.Id, Arg.Any<BudgetScopeKind>(), Arg.Any<BudgetCapKind>(), Arg.Any<decimal>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                held.TrySetResult();
                return Task.CompletedTask;
            });

        // Client month-to-date spend already at the soft cap, so the new job must be held rather than started.
        var capsProvider = Substitute.For<IBudgetCapsProvider>();
        capsProvider.GetCapsAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(new BudgetCaps(80m, 100m, null, null, null, null));
        var accumulator = Substitute.For<IReviewSpendAccumulator>();
        accumulator.GetBaselineAsync(
                Arg.Is<ReviewSpendSubject>(subject => subject.UnitOfWorkId == job.Id),
                Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(new ReviewSpendBaseline(new ReviewScopeSpend(80m, false), ReviewScopeSpend.None, ReviewScopeSpend.None));

        // The held transition also emits a budget event for a downstream alerting capability.
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventPublisher = Substitute.For<IBudgetEventPublisher>();
        eventPublisher.PublishAsync(Arg.Any<BudgetEventNotification>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                published.TrySetResult();
                return Task.CompletedTask;
            });

        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IBudgetCapsProvider)).Returns(capsProvider);
        sp.GetService(typeof(IReviewSpendAccumulator)).Returns(accumulator);
        sp.GetService(typeof(IBudgetEventPublisher)).Returns(eventPublisher);
        sp.GetService(typeof(IReviewJobProcessor)).Returns(Substitute.For<IReviewJobProcessor>());

        var worker = new ReviewJobWorker(
            scopeFactory, CreateWorkerOptions(), CreateLeaseOptions(),
            CreateMetrics(), CreateCancellationRegistry(),
            TimeProvider.System, logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = worker.StartAsync(cts.Token);
        await Task.WhenAll(held.Task, published.Task).WaitAsync(TimeSpan.FromSeconds(2));

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        await repo.Received().SetBudgetHeldAsync(job.Id, BudgetScopeKind.ClientMonthly, BudgetCapKind.Soft, 80m, 80m, Arg.Any<CancellationToken>());
        await leaseStore.DidNotReceive()
            .TryClaimAsync(job.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await eventPublisher.Received().PublishAsync(
            Arg.Is<BudgetEventNotification>(n =>
                n.EventType == BudgetEventType.SoftCapReached &&
                n.Scope == BudgetScopeKind.ClientMonthly &&
                n.ClientId == job.ClientId &&
                n.JobId == job.Id),
            Arg.Any<CancellationToken>());
    }

    // A window the fleet skip emptied completely used to end the cycle, so a tenant with no runner whose job
    // sat behind another tenant's runner-backed backlog was not considered at all until that backlog drained
    // below the window size.
    [Fact]
    public async Task AWindowFullOfRunnerReservedJobs_DoesNotStarveTheTenantBehindIt()
    {
        var runnerClient = Guid.NewGuid();
        var reservedJobs = Enumerable.Range(0, 3)
            .Select(i => new ReviewJob(Guid.NewGuid(), runnerClient, "https://dev.azure.com/org", "proj", "repo", 300 + i, 1))
            .ToArray();
        var starvedJob = CreateJob(400);

        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseStore = Substitute.For<IReviewJobLeaseStore>();
        leaseStore.GetClaimCandidatesAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<ReviewJob>>(call.ArgAt<DateTimeOffset?>(1) is null ? reservedJobs : [starvedJob]));
        leaseStore.TryClaimAsync(starvedJob.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewJobLease(starvedJob.Id, "host", 1, DateTimeOffset.UtcNow.AddMinutes(2)));
        leaseStore.When(store => store.TryClaimAsync(starvedJob.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()))
            .Do(_ => claimed.TrySetResult());
        leaseStore.TryRenewAsync(Arg.Any<ReviewJobLease>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewJobLeaseRenewal(true, DateTimeOffset.UtcNow.AddMinutes(2)));

        var fleetMonitor = Substitute.For<IRunnerFleetMonitor>();
        fleetMonitor.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new RunnerFleetStatus(ReviewExecutionMode.RunnersOnly, 1, null, new HashSet<Guid> { runnerClient }));

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(Substitute.For<IReviewJobExecutionStore>());
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IRunnerFleetMonitor)).Returns(fleetMonitor);
        sp.GetService(typeof(IReviewJobProcessor)).Returns(Substitute.For<IReviewJobProcessor>());

        var worker = new ReviewJobWorker(
            scopeFactory,
            CreateWorkerOptions(),
            Options.Create(new ReviewLeaseOptions { ClaimCandidateLimit = reservedJobs.Length }),
            CreateMetrics(), CreateCancellationRegistry(),
            TimeProvider.System, Substitute.For<ILogger<ReviewJobWorker>>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = worker.StartAsync(cts.Token);
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        // The reserved jobs stayed reserved: only the tenant behind them was claimed.
        await leaseStore.DidNotReceive()
            .TryClaimAsync(reservedJobs[0].Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // A publishing pr_wide pass never dispatches to a runner, so leaving its jobs "for a runner" strands
    // them un-reviewed while every lease offer refuses the manifest. Running them in process is the one
    // deliberate exception to the no-fallback rule.
    [Fact]
    public async Task AJobNoRunnerCanExecute_RunsInProcess_WhenItsPassListPublishesPrWide()
    {
        var prWideClient = Guid.NewGuid();
        var job = new ReviewJob(Guid.NewGuid(), prWideClient, "https://dev.azure.com/org", "proj", "repo", 500, 1);

        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseStore = CreateLeaseStore(job);
        leaseStore.When(store => store.TryClaimAsync(job.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()))
            .Do(_ => claimed.TrySetResult());

        var fleetMonitor = Substitute.For<IRunnerFleetMonitor>();
        fleetMonitor.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(new RunnerFleetStatus(ReviewExecutionMode.RunnersOnly, 1, null, new HashSet<Guid> { prWideClient }));
        var clients = Substitute.For<MeisterDev.ProPR.Application.Interfaces.IClientRegistry>();
        clients.GetReviewPassesAsync(prWideClient, Arg.Any<CancellationToken>())
            .Returns([new MeisterDev.ProPR.Application.ValueObjects.ReviewPassSpec(Guid.NewGuid(), Scope: ReviewPassScope.PrWide, Shadow: false)]);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(Substitute.For<IReviewJobExecutionStore>());
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IRunnerFleetMonitor)).Returns(fleetMonitor);
        sp.GetService(typeof(MeisterDev.ProPR.Application.Interfaces.IClientRegistry)).Returns(clients);
        sp.GetService(typeof(IReviewJobProcessor)).Returns(Substitute.For<IReviewJobProcessor>());

        var worker = new ReviewJobWorker(
            scopeFactory, CreateWorkerOptions(), CreateLeaseOptions(),
            CreateMetrics(), CreateCancellationRegistry(),
            TimeProvider.System, Substitute.For<ILogger<ReviewJobWorker>>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = worker.StartAsync(cts.Token);
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cts.CancelAsync();
        await worker.StopAsync(CancellationToken.None);

        await leaseStore.Received()
            .TryClaimAsync(job.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_ClaimsPendingJobAndTransitionsToProcessing()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var job = CreateJob(101);
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseStore = CreateLeaseStore(job);
        leaseStore.When(store => store.TryClaimAsync(job.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()))
            .Do(_ => claimed.TrySetResult());

        var logger = Substitute.For<ILogger<ReviewJobWorker>>();

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);

        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        // Make the Reviewing processor return null — GetRequiredService will throw,
        // causing SetFailed to be called on the job.
        sp.GetService(typeof(IReviewJobProcessor)).Returns(null);

        var worker = new ReviewJobWorker(
            scopeFactory, CreateWorkerOptions(), CreateLeaseOptions(),
            CreateMetrics(), CreateCancellationRegistry(),
            TimeProvider.System, logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        _ = worker.StartAsync(cts.Token);
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);

        // The job should have been picked up, which means a lease was claimed for it.
        await leaseStore.Received()
            .TryClaimAsync(job.Id, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenJobCancelledViaRegistry_MarksStoppedAndDoesNotRequeue()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var job = CreateJob(555);
        var processingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var leaseStore = CreateLeaseStore(job);
        repo.SetStoppedAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stopped.TrySetResult();
                return Task.CompletedTask;
            });

        var processor = Substitute.For<IReviewJobProcessor>();
        processor.ProcessAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var token = ci.Arg<CancellationToken>();
                processingStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            });

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IReviewJobProcessor)).Returns(processor);

        var registry = new ReviewJobCancellationRegistry();
        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(
            scopeFactory, CreateWorkerOptions(), CreateLeaseOptions(),
            CreateMetrics(), registry, TimeProvider.System, logger);

        using var hostCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = worker.StartAsync(hostCts.Token);
        await processingStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Manual stop: cancel via the registry while the host-shutdown token stays live.
        Assert.True(registry.Cancel(job.Id));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await worker.StopAsync(CancellationToken.None);

        await repo.Received(1).SetStoppedAsync(job.Id, Arg.Any<CancellationToken>());
        await leaseStore.DidNotReceive().TryReleaseAsync(Arg.Any<ReviewJobLease>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_IsRunning_BecomesFalseAfterStop()
    {
        var scopeFactory = CreateScopeFactory();
        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(
            scopeFactory, CreateWorkerOptions(), CreateLeaseOptions(),
            CreateMetrics(), CreateCancellationRegistry(),
            TimeProvider.System, logger);

        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);
        await WaitUntilAsync(
            () => worker.IsRunning,
            TimeSpan.FromSeconds(1),
            "Worker never entered the running state.");

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
        var leaseStore = CreateLeaseStore(job);
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
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        // GetRequiredService throws — simulating unhandled exception in the Reviewing processor.
        sp.GetService(typeof(IReviewJobProcessor)).Returns(null);

        var logger = Substitute.For<ILogger<ReviewJobWorker>>();
        var worker = new ReviewJobWorker(
            scopeFactory, CreateWorkerOptions(), CreateLeaseOptions(),
            CreateMetrics(), CreateCancellationRegistry(),
            TimeProvider.System, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        _ = worker.StartAsync(cts.Token);

        await jobFailed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        // Worker should still be running despite the exception
        Assert.True(worker.IsRunning);

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }

    // Fault isolation is only half the job: the worker surviving is useless if the job it failed says nothing an
    // operator can act on. A provider failure carries the profile, the model and what to try next, and that is
    // what has to reach the recorded failure reason.
    [Fact]
    public async Task Worker_WhenProviderCallFails_RecordsACauseThatNamesTheProfile()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var job = CreateJob(911);
        var jobFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? recordedReason = null;
        var leaseStore = CreateLeaseStore(job);
        repo.SetFailedAsync(job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                recordedReason = call.ArgAt<string>(1);
                jobFailed.TrySetResult();
                return Task.CompletedTask;
            });

        var processor = Substitute.For<IReviewJobProcessor>();
        processor.ProcessAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(
                new ProviderCallFailedException(
                    new ProviderCallTarget(AiProviderKind.OpenAiCompatible, "deepseek-reasoner", "Primary DeepSeek"),
                    ProviderFailureVerdict.Permanent("The provider rejected the credential (HTTP 401).", 401),
                    1,
                    "Check the configured API key or credential source.")));

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IReviewJobProcessor)).Returns(processor);

        var worker = new ReviewJobWorker(
            scopeFactory,
            CreateWorkerOptions(),
            CreateLeaseOptions(),
            CreateMetrics(),
            CreateCancellationRegistry(),
            TimeProvider.System,
            Substitute.For<ILogger<ReviewJobWorker>>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        _ = worker.StartAsync(cts.Token);
        await jobFailed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(worker.IsRunning);
        Assert.NotNull(recordedReason);
        Assert.Contains("Primary DeepSeek", recordedReason, StringComparison.Ordinal);
        Assert.Contains("API key", recordedReason, StringComparison.Ordinal);

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_WhenProcessorMissing_MarksClaimedJobFailed()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var job = CreateJob(909);
        var jobFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaseStore = CreateLeaseStore(job);
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
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IReviewJobProcessor)).Returns((object?)null);

        var worker = new ReviewJobWorker(
            scopeFactory,
            CreateWorkerOptions(),
            CreateLeaseOptions(),
            CreateMetrics(),
            CreateCancellationRegistry(),
            TimeProvider.System,
            Substitute.For<ILogger<ReviewJobWorker>>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        _ = worker.StartAsync(cts.Token);
        await jobFailed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await repo.Received().SetFailedAsync(job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_CapsConcurrentInFlightJobs_WhenParallelExecutionLicensed()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var jobs = Enumerable.Range(1, 5).Select(i => CreateJob(2000 + i)).ToArray();
        var leaseStore = CreateLeaseStore(jobs);

        var startedCount = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IReviewJobProcessor>();
        processor.ProcessAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref startedCount);
                await release.Task;
            });

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IReviewJobProcessor)).Returns(processor);

        // No ILicensingCapabilityService registered -> parallel review execution is treated as enabled,
        // so only the concurrency cap (2) should bound how many of the 5 pending jobs run at once.
        var worker = new ReviewJobWorker(
            scopeFactory,
            CreateWorkerOptions(maxConcurrentReviewJobs: 2),
            CreateLeaseOptions(),
            CreateMetrics(),
            CreateCancellationRegistry(),
            TimeProvider.System,
            Substitute.For<ILogger<ReviewJobWorker>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = worker.StartAsync(cts.Token);

        try
        {
            await WaitUntilAsync(
                () => Volatile.Read(ref startedCount) >= 2,
                TimeSpan.FromSeconds(2),
                "Worker never started the capped number of jobs.");

            // Let several more poll cycles elapse; the cap must keep the started count pinned at 2
            // while the in-flight jobs stay blocked.
            await Task.Delay(200, CancellationToken.None);

            Assert.Equal(2, Volatile.Read(ref startedCount));
        }
        finally
        {
            release.TrySetResult();
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // The isolation guarantee the whole feature rests on: with a live runner fleet this process executes
    // nothing at all. Asserted on the processor rather than on a log line, because the guarantee is about
    // where the work runs and nothing else proves that.
    [Fact]
    public async Task Worker_ExecutesNothingItself_WhileARunnerFleetIsActive()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var covered = new[] { CreateJob(4001), CreateJob(4002) };
        var leaseStore = CreateLeaseStore(covered);
        var processor = Substitute.For<IReviewJobProcessor>();

        // Signalled by the monitor itself, so the assertions run only once the worker has actually reached
        // the fleet check. A fixed delay would pass on a worker that faulted before getting there, which
        // is the one outcome this test must not report as success.
        var fleetConsulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fleet = Substitute.For<IRunnerFleetMonitor>();
        fleet.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                fleetConsulted.TrySetResult();
                return Task.FromResult(
                    new RunnerFleetStatus(
                        ReviewExecutionMode.RunnersOnly,
                        2,
                        null,
                        covered.Select(job => job.ClientId).ToHashSet()));
            });

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IReviewJobProcessor)).Returns(processor);
        sp.GetService(typeof(IRunnerFleetMonitor)).Returns(fleet);

        var worker = new ReviewJobWorker(
            scopeFactory,
            CreateWorkerOptions(maxConcurrentReviewJobs: 4),
            CreateLeaseOptions(),
            CreateMetrics(),
            CreateCancellationRegistry(),
            TimeProvider.System,
            Substitute.For<ILogger<ReviewJobWorker>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var running = worker.StartAsync(cts.Token);

        try
        {
            await fleetConsulted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(running.IsFaulted, running.Exception?.ToString());

            await processor.DidNotReceiveWithAnyArgs().ProcessAsync(default!, default);

            // The jobs are also left alone rather than failed, so a runner can still take them.
            await leaseStore.DidNotReceiveWithAnyArgs().TryClaimAsync(default, default!, default, default);
        }
        finally
        {
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // Runners are scoped to clients and the installation is not, so one tenant running runners must not
    // stop every other tenant's reviews. Those jobs can never be offered to a runner outside their tenant;
    // suppressing them here too would leave them pending forever while the fleet looks perfectly healthy.
    [Fact]
    public async Task Worker_StillExecutes_ForAClientNoActiveRunnerCanServe()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var strandedJob = CreateJob(4101);
        var leaseStore = CreateLeaseStore(strandedJob);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IReviewJobProcessor>();
        processor.ProcessAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                started.TrySetResult();
                return Task.CompletedTask;
            });

        // A live fleet, but every active runner belongs to some other tenant's clients.
        var fleet = Substitute.For<IRunnerFleetMonitor>();
        fleet.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(
                new RunnerFleetStatus(
                    ReviewExecutionMode.RunnersOnly,
                    1,
                    null,
                    new HashSet<Guid> { Guid.NewGuid() }));

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IReviewJobProcessor)).Returns(processor);
        sp.GetService(typeof(IRunnerFleetMonitor)).Returns(fleet);

        var worker = new ReviewJobWorker(
            scopeFactory,
            CreateWorkerOptions(maxConcurrentReviewJobs: 2),
            CreateLeaseOptions(),
            CreateMetrics(),
            CreateCancellationRegistry(),
            TimeProvider.System,
            Substitute.For<ILogger<ReviewJobWorker>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = worker.StartAsync(cts.Token);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // Without the capability the configured cap is not merely ignored, it is replaced by one. A deployment
    // that raises the setting to get parallelism back gets a single review either way.
    [Fact]
    public async Task Worker_RunsOneJobAtATime_WhenParallelExecutionIsNotLicensed()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var jobs = Enumerable.Range(1, 5).Select(i => CreateJob(3000 + i)).ToArray();
        var leaseStore = CreateLeaseStore(jobs);

        var startedCount = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IReviewJobProcessor>();
        processor.ProcessAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref startedCount);
                await release.Task;
            });

        var licensing = Substitute.For<ILicensingCapabilityService>();
        licensing.IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(false));

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IReviewJobProcessor)).Returns(processor);
        sp.GetService(typeof(ILicensingCapabilityService)).Returns(licensing);

        var worker = new ReviewJobWorker(
            scopeFactory,
            CreateWorkerOptions(maxConcurrentReviewJobs: 4),
            CreateLeaseOptions(),
            CreateMetrics(),
            CreateCancellationRegistry(),
            TimeProvider.System,
            Substitute.For<ILogger<ReviewJobWorker>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = worker.StartAsync(cts.Token);

        try
        {
            await WaitUntilAsync(
                () => Volatile.Read(ref startedCount) >= 1,
                TimeSpan.FromSeconds(2),
                "Worker never started a job.");

            // Several more poll cycles: the count must stay pinned at one while that review is blocked,
            // even though four were configured and five are pending.
            await Task.Delay(200, CancellationToken.None);

            Assert.Equal(1, Volatile.Read(ref startedCount));
        }
        finally
        {
            release.TrySetResult();
            cts.Cancel();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    // The behaviour that replaces failing jobs by age: a running job is protected by its live lease, so a
    // sweep on this or any other host leaves it alone however long it takes. Nothing consults elapsed
    // processing time any more, and the exemption set that used to protect it is gone with it.
    [Fact]
    public async Task Worker_LeavesARunningJobAlone_WhileItsLeaseIsCurrent()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var job = CreateJob(1001);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var leaseStore = CreateLeaseStore(job);

        var processor = Substitute.For<IReviewJobProcessor>();
        processor.ProcessAsync(job, Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                started.TrySetResult();
                await release.Task;
            });

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IReviewJobProcessor)).Returns(processor);

        var worker = new ReviewJobWorker(
            scopeFactory,
            CreateWorkerOptions(25),
            CreateLeaseOptions(),
            CreateMetrics(),
            CreateCancellationRegistry(),
            TimeProvider.System,
            Substitute.For<ILogger<ReviewJobWorker>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = worker.StartAsync(cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(150, CancellationToken.None);

        await repo.DidNotReceive().SetFailedAsync(job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());

        release.TrySetResult();
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }

    // A stop issued on another host reaches this one only through the heartbeat. The persisted status
    // already says the job is stopped, so this host must leave it alone: releasing the lease would put a
    // deliberately halted review straight back in the queue.
    [Fact]
    public async Task Worker_WhenAStopDirectiveArrives_DoesNotRequeueOrFailTheJob()
    {
        var repo = Substitute.For<IReviewJobExecutionStore>();
        var job = CreateJob(4242);
        var processingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var leaseStore = CreateLeaseStore(job);
        leaseStore.TryRenewAsync(Arg.Any<ReviewJobLease>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.OperatorStop));

        var processor = Substitute.For<IReviewJobProcessor>();
        processor.ProcessAsync(Arg.Any<ReviewJob>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var token = ci.Arg<CancellationToken>();
                processingStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            });

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(repo);
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IReviewJobProcessor)).Returns(processor);

        // A one-second heartbeat so the directive lands inside the test rather than two minutes later.
        var leaseOptions = Options.Create(
            new ReviewLeaseOptions
            {
                LeaseDurationSeconds = 30,
                HeartbeatIntervalSeconds = 5,
                HeartbeatJitterFraction = 0,
            });

        var worker = new ReviewJobWorker(
            scopeFactory,
            CreateWorkerOptions(),
            leaseOptions,
            CreateMetrics(),
            CreateCancellationRegistry(),
            TimeProvider.System,
            Substitute.For<ILogger<ReviewJobWorker>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = worker.StartAsync(cts.Token);
        await processingStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitUntilAsync(
            () => leaseStore.ReceivedCalls().Any(call =>
                string.Equals(call.GetMethodInfo().Name, nameof(IReviewJobLeaseStore.TryRenewAsync), StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10),
            "The heartbeat never renewed, so no directive could arrive.");

        await Task.Delay(300, CancellationToken.None);

        await leaseStore.DidNotReceive().TryReleaseAsync(Arg.Any<ReviewJobLease>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().SetFailedAsync(job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }

    // Reclaim runs on the sweep, and the job it takes back is decided by the store, not by the worker
    // reasoning about ages. What the worker owes is that the sweep happens at all, including at startup.
    [Fact]
    public async Task Worker_SweepsForExpiredLeases_AtStartup()
    {
        var expired = new ExpiredReviewJobLease(Guid.NewGuid(), 3, DateTimeOffset.UtcNow.AddMinutes(-5));
        var swept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var leaseStore = CreateLeaseStore();
        leaseStore.GetExpiredLeasesAsync(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExpiredReviewJobLease>>([expired]));
        leaseStore.TryReclaimAsync(expired, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                swept.TrySetResult();
                return Task.FromResult(ReviewJobReclaimOutcome.Requeued);
            });

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var sp = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(sp);
        sp.GetService(typeof(IReviewJobExecutionStore)).Returns(Substitute.For<IReviewJobExecutionStore>());
        sp.GetService(typeof(IReviewJobLeaseStore)).Returns(leaseStore);
        sp.GetService(typeof(IReviewJobProcessor)).Returns(Substitute.For<IReviewJobProcessor>());

        var worker = new ReviewJobWorker(
            scopeFactory,
            CreateWorkerOptions(),
            CreateLeaseOptions(),
            CreateMetrics(),
            CreateCancellationRegistry(),
            TimeProvider.System,
            Substitute.For<ILogger<ReviewJobWorker>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = worker.StartAsync(cts.Token);

        await swept.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);
    }
}
