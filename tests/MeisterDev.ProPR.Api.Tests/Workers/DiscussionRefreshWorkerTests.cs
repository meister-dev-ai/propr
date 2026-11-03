// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using MeisterDev.ProPR.Api.Telemetry;
using MeisterDev.ProPR.Api.Workers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Workers;

public sealed class DiscussionRefreshWorkerTests
{
    [Fact]
    public async Task TransientDueListingFailureDoesNotStopTheWorker()
    {
        var store = Substitute.For<IDiscussionSynchronizationStore>();
        var attempts = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ListDueAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("transient");
            }

            recovered.TrySetResult();
            return Task.FromResult<IReadOnlyList<DiscussionSynchronizationState>>([]);
        });
        await using var services = Services(store).BuildServiceProvider();
        using var metrics = new ReviewJobMetrics(services.GetRequiredService<IServiceScopeFactory>());
        using var worker = Worker(services, metrics);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(attempts >= 2);
    }

    [Fact]
    public async Task UnavailableConnectionDefersTheClaimedGenerationWithoutAdmittingReviewWork()
    {
        var store = Substitute.For<IDiscussionSynchronizationStore>();
        var state = State();
        store.ListDueAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([state]);
        var synchronization = Substitute.For<IPullRequestSynchronizationService>();
        var builder = Services(store);
        builder.AddSingleton(synchronization);
        await using var services = builder.BuildServiceProvider();
        using var metrics = new ReviewJobMetrics(services.GetRequiredService<IServiceScopeFactory>());
        using var worker = Worker(services, metrics);
        await worker.RunCycleAsync(CancellationToken.None);
        await worker.DrainAsync();
        await store.Received(1).DeferAsync(state.Id, state.RequestedGeneration, TimeSpan.FromSeconds(300), Arg.Any<CancellationToken>());
        Assert.Empty(synchronization.ReceivedCalls());
    }

    [Fact]
    public async Task SlowClientDoesNotBlockAnotherClientAndEachRefreshUsesItsOwnServiceScope()
    {
        var store = Substitute.For<IDiscussionSynchronizationStore>();
        var slow = State();
        var fast = State();
        store.ListDueAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([slow, fast]);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ClientScmConnectionDto(
                call.ArgAt<Guid>(1), call.ArgAt<Guid>(0), ScmProvider.AzureDevOps, "https://dev.azure.com/org",
                ScmAuthenticationKind.PersonalAccessToken, "Test", true, "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var scopes = new ConcurrentBag<Guid>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = Services(store);
        builder.AddSingleton(connections);
        builder.AddScoped(_ =>
        {
            var identity = Guid.NewGuid();
            var service = Substitute.For<IPullRequestSynchronizationService>();
            service.SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                scopes.Add(identity);
                var request = call.Arg<PullRequestSynchronizationRequest>();
                Assert.True(request.DiscussionOnly);
                Assert.False(request.AllowReviewSubmission);
                if (request.ClientId == slow.ClientId)
                {
                    await release.Task.WaitAsync(call.Arg<CancellationToken>());
                }
                else
                {
                    progressed.TrySetResult();
                }

                return new PullRequestSynchronizationOutcome(
                    PullRequestSynchronizationReviewDecision.None, PullRequestSynchronizationLifecycleDecision.None, []);
            });
            return service;
        });
        await using var services = builder.BuildServiceProvider();
        using var metrics = new ReviewJobMetrics(services.GetRequiredService<IServiceScopeFactory>());
        using var worker = Worker(services, metrics);
        var running = worker.RunCycleAsync(CancellationToken.None);
        try
        {
            await progressed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
        }

        await running;
        await worker.DrainAsync();
        Assert.Equal(2, scopes.Distinct().Count());
    }

    [Fact]
    public async Task ClientBecomingDueDuringAnotherClientsBatchRunsBeforeTheBlockedClientCompletes()
    {
        var store = Substitute.For<IDiscussionSynchronizationStore>();
        var slow = State();
        var fast = State();
        var largeBatch = Enumerable.Range(1, 128).Select(pr => new DiscussionSynchronizationState
        {
            Id = Guid.NewGuid(), ClientId = slow.ClientId, ConnectionId = slow.ConnectionId, Provider = slow.Provider,
            ProviderScopePath = slow.ProviderScopePath, ProviderProjectKey = slow.ProviderProjectKey,
            RepositoryId = slow.RepositoryId, PullRequestId = pr, RequestedGeneration = 1,
        }).ToArray();
        var includeFast = false;
        store.ListDueAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ =>
            includeFast ? (IReadOnlyList<DiscussionSynchronizationState>)[fast, .. largeBatch.Take(127)] : largeBatch);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
            new ClientScmConnectionDto(
                call.ArgAt<Guid>(1), call.ArgAt<Guid>(0), ScmProvider.AzureDevOps, "https://dev.azure.com/org",
                ScmAuthenticationKind.PersonalAccessToken, "Test", true, "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCalls = 0;
        var service = Substitute.For<IPullRequestSynchronizationService>();
        service.SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (call.Arg<PullRequestSynchronizationRequest>().ClientId == slow.ClientId)
            {
                Interlocked.Increment(ref slowCalls);
                started.TrySetResult();
                await release.Task.WaitAsync(call.Arg<CancellationToken>());
            }
            else
            {
                progressed.TrySetResult();
            }

            return new PullRequestSynchronizationOutcome(PullRequestSynchronizationReviewDecision.None, PullRequestSynchronizationLifecycleDecision.None, []);
        });
        var builder = Services(store);
        builder.AddSingleton(connections);
        builder.AddSingleton(service);
        await using var services = builder.BuildServiceProvider();
        using var metrics = new ReviewJobMetrics(services.GetRequiredService<IServiceScopeFactory>());
        using var worker = Worker(services, metrics);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            includeFast = true;
            await progressed.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(1, Volatile.Read(ref slowCalls));
        }
        finally
        {
            release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterClosedObservationDefersLifecycleCompletion(bool generationAdvancedBeforeObservation)
    {
        var store = Substitute.For<IDiscussionSynchronizationStore>();
        var state = State();
        store.ListDueAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([state]);
        var completedGeneration = state.RequestedGeneration + (generationAdvancedBeforeObservation ? 1 : 0);
        store.ReadAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<CancellationToken>())
            .Returns(new DiscussionSynchronizationState { Id = state.Id, RequestedGeneration = completedGeneration, IsClosed = true });
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByIdAsync(state.ClientId, state.ConnectionId, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionDto(
                state.ConnectionId, state.ClientId, state.Provider, state.ProviderScopePath,
                ScmAuthenticationKind.PersonalAccessToken, "Test", true, "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var service = Substitute.For<IPullRequestSynchronizationService>();
        service.SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            state.IsClosed = true;
            return Task.FromException<PullRequestSynchronizationOutcome>(new InvalidOperationException("job cancellation failed after observation"));
        });
        var builder = Services(store);
        builder.AddSingleton(connections);
        builder.AddSingleton(service);
        await using var services = builder.BuildServiceProvider();
        using var metrics = new ReviewJobMetrics(services.GetRequiredService<IServiceScopeFactory>());
        using var worker = Worker(services, metrics);
        await worker.RunCycleAsync(CancellationToken.None);
        await worker.DrainAsync();
        await store.Received(1).DeferAfterFailureAsync(state.Id, completedGeneration, state.ConsecutiveFailures, Arg.Any<CancellationToken>());
        await store.DidNotReceive().DeferAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARefreshThatCouldNotReadTheDiscussionCountsAsAFailedAttempt(bool discussionUnavailable)
    {
        var store = Substitute.For<IDiscussionSynchronizationStore>();
        var state = State();
        state.ConsecutiveFailures = 3;
        store.ListDueAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([state]);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByIdAsync(state.ClientId, state.ConnectionId, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionDto(
                state.ConnectionId, state.ClientId, state.Provider, state.ProviderScopePath,
                ScmAuthenticationKind.PersonalAccessToken, "Test", true, "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var service = Substitute.For<IPullRequestSynchronizationService>();
        service.SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>()).Returns(
            new PullRequestSynchronizationOutcome(PullRequestSynchronizationReviewDecision.None, PullRequestSynchronizationLifecycleDecision.None, [])
            {
                DiscussionUnavailable = discussionUnavailable,
            });
        var builder = Services(store);
        builder.AddSingleton(connections);
        builder.AddSingleton(service);
        await using var services = builder.BuildServiceProvider();
        using var metrics = new ReviewJobMetrics(services.GetRequiredService<IServiceScopeFactory>());
        using var worker = Worker(services, metrics);

        await worker.RunCycleAsync(CancellationToken.None);
        await worker.DrainAsync();

        // The deferral names the generation of this attempt and the failures it started with, so the store can tell an
        // attempt that the lease already counted from one that failed before it.
        await store.Received(discussionUnavailable ? 1 : 0).DeferAfterFailureAsync(state.Id, state.RequestedGeneration, 3, Arg.Any<CancellationToken>());
        await store.DidNotReceive().DeferAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownAfterClosedObservationPreservesPendingLifecycleWork(bool generationAdvancedBeforeObservation)
    {
        var state = State();
        var store = Substitute.For<IDiscussionSynchronizationStore>();
        store.ListDueAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([state]);
        var completedGeneration = state.RequestedGeneration + (generationAdvancedBeforeObservation ? 1 : 0);
        store.ReadAsync(Arg.Any<PullRequestThreadSnapshotTarget>(), Arg.Any<CancellationToken>())
            .Returns(new DiscussionSynchronizationState { Id = state.Id, RequestedGeneration = completedGeneration, IsClosed = true });
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByIdAsync(state.ClientId, state.ConnectionId, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionDto(
                state.ConnectionId, state.ClientId, state.Provider, state.ProviderScopePath, ScmAuthenticationKind.PersonalAccessToken,
                "Test", true, "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        using var cancellation = new CancellationTokenSource();
        var synchronization = Substitute.For<IPullRequestSynchronizationService>();
        synchronization.SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            state.IsClosed = true;
            cancellation.Cancel();
            return Task.FromCanceled<PullRequestSynchronizationOutcome>(cancellation.Token);
        });
        var builder = Services(store);
        builder.AddSingleton(connections);
        builder.AddSingleton(synchronization);
        await using var services = builder.BuildServiceProvider();
        using var metrics = new ReviewJobMetrics(services.GetRequiredService<IServiceScopeFactory>());
        using var worker = Worker(services, metrics);
        await worker.RunCycleAsync(cancellation.Token);
        await worker.DrainAsync();
        await store.Received(1).DeferAsync(
            state.Id, completedGeneration, TimeSpan.FromSeconds(15), Arg.Is<CancellationToken>(ct => !ct.IsCancellationRequested));
    }

    [Fact]
    public async Task FullWorkerCapacitySkipsDueDatabaseQueries()
    {
        var state = State();
        var store = Substitute.For<IDiscussionSynchronizationStore>();
        store.ListDueAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([state]);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByIdAsync(state.ClientId, state.ConnectionId, Arg.Any<CancellationToken>()).Returns(
            new ClientScmConnectionDto(
                state.ConnectionId, state.ClientId, state.Provider, state.ProviderScopePath, ScmAuthenticationKind.PersonalAccessToken,
                "Test", true, "verified", null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronization = Substitute.For<IPullRequestSynchronizationService>();
        synchronization.SynchronizeAsync(Arg.Any<PullRequestSynchronizationRequest>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return new PullRequestSynchronizationOutcome(PullRequestSynchronizationReviewDecision.None, PullRequestSynchronizationLifecycleDecision.None, []);
        });
        var builder = Services(store);
        builder.AddSingleton(connections);
        builder.AddSingleton(synchronization);
        await using var services = builder.BuildServiceProvider();
        using var metrics = new ReviewJobMetrics(services.GetRequiredService<IServiceScopeFactory>());
        using var worker = Worker(services, metrics, new DiscussionSynchronizationOptions { GlobalConcurrency = 1 });
        await worker.RunCycleAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            store.ClearReceivedCalls();
            await worker.RunCycleAsync(CancellationToken.None);
            await store.DidNotReceive().ListDueAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            release.TrySetResult();
            await worker.DrainAsync();
        }
    }

    private static ServiceCollection Services(IDiscussionSynchronizationStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddSingleton(LocalScmPolicies.Registry);
        services.AddSingleton(Substitute.For<IJobRepository>());
        return services;
    }

    private static DiscussionRefreshWorker Worker(IServiceProvider services, ReviewJobMetrics metrics, DiscussionSynchronizationOptions? configuration = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(configuration ?? new DiscussionSynchronizationOptions());
        return new(
            services.GetRequiredService<IServiceScopeFactory>(), new FairCrawlWorkScheduler(options), options,
            NullLogger<DiscussionRefreshWorker>.Instance, metrics);
    }

    private static DiscussionSynchronizationState State() => new()
    {
        Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ConnectionId = Guid.NewGuid(), Provider = ScmProvider.AzureDevOps,
        ProviderScopePath = "https://dev.azure.com/org", ProviderProjectKey = "project", RepositoryId = "repo", PullRequestId = 42, RequestedGeneration = 7,
    };
}
