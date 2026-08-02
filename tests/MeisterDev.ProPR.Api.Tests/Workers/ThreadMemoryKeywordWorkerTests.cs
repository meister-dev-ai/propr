// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Workers;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Workers;

/// <summary>
///     The keyword back-fill worker. Every row it sweeps costs a model call, so what matters is that a host
///     which never asked for the back-fill spends nothing, and that a missing or failing sweeper cannot take the
///     host down.
/// </summary>
/// <remarks>
///     The tests that expect a sweep wait for the sweeper to be called rather than assuming it happened by the
///     time the host reports the worker started. An earlier version assumed that and passed alone while failing
///     inside a loaded solution-wide run.
/// </remarks>
public sealed class ThreadMemoryKeywordWorkerTests
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ExecuteAsync_WithTheDefaultBudget_NeverResolvesTheSweeper()
    {
        var sweeper = Substitute.For<IThreadMemoryKeywordSweeper>();

        // A zero budget is decided before any await, so the guard has run by the time the host reports started.
        // Zero is the default: resolving a scope per sweep on every host with a database would be work nobody
        // asked for.
        await RunAsync(new ThreadMemoryKeywordOptions(), ScopeFactoryFor(sweeper), signal: null);

        await sweeper.DidNotReceive().SweepAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithABudget_SweepsThatMany()
    {
        var called = new TaskCompletionSource();
        var sweeper = Substitute.For<IThreadMemoryKeywordSweeper>();
        sweeper.SweepAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                called.TrySetResult();
                return Task.FromResult(3);
            });

        Assert.True(await RunAsync(new ThreadMemoryKeywordOptions { BackfillMax = 25 }, ScopeFactoryFor(sweeper), called.Task));

        await sweeper.Received(1).SweepAsync(25, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheSweeperIsNotRegistered_DoesNotThrow()
    {
        // The sweeper lives behind the same database gate as the rest of its module. A host with a budget set
        // but no sweeper registered must log and carry on, not fail to start.
        var exception = await Record.ExceptionAsync(() =>
            RunAsync(new ThreadMemoryKeywordOptions { BackfillMax = 5 }, ScopeFactoryFor(sweeper: null), signal: null));
        Assert.Null(exception);
    }

    [Fact]
    public async Task ExecuteAsync_WhenASweepThrows_DoesNotTearDownTheLoop()
    {
        var called = new TaskCompletionSource();
        var sweeper = Substitute.For<IThreadMemoryKeywordSweeper>();
        sweeper.SweepAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ =>
            {
                called.TrySetResult();
                throw new InvalidOperationException("the database went away");
            });

        Assert.True(await RunAsync(new ThreadMemoryKeywordOptions { BackfillMax = 5 }, ScopeFactoryFor(sweeper), called.Task));
    }

    /// <summary>
    ///     Starts the worker, waits for <paramref name="signal" /> if one is given, then stops it. Returns whether
    ///     the signal arrived; a null signal means the assertion is a negative one and there is nothing to wait for.
    /// </summary>
    private static async Task<bool> RunAsync(
        ThreadMemoryKeywordOptions options,
        IServiceScopeFactory scopeFactory,
        Task? signal)
    {
        var worker = new ThreadMemoryKeywordWorker(
            scopeFactory,
            new StaticOptionsMonitor<ThreadMemoryKeywordOptions>(options),
            NullLogger<ThreadMemoryKeywordWorker>.Instance);

        using var cancellation = new CancellationTokenSource();
        await worker.StartAsync(cancellation.Token);

        var arrived = signal is null || await Task.WhenAny(signal, Task.Delay(CallTimeout)) == signal;

        await cancellation.CancelAsync();
        await worker.StopAsync(CancellationToken.None);
        return arrived;
    }

    private static IServiceScopeFactory ScopeFactoryFor(IThreadMemoryKeywordSweeper? sweeper)
    {
        var services = new ServiceCollection();
        if (sweeper is not null)
        {
            services.AddScoped(_ => sweeper);
        }

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
