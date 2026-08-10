// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Workers;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Workers;

/// <summary>
///     Removing runners that stopped calling in. The sweep exists because a restarted host enrolls again
///     as a new runner, so under a deployment that scales itself the registry grows without limit. The
///     cases covered here include the rows the sweep must leave in place.
/// </summary>
public sealed class RunnerRegistryPruneWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly IRunnerRegistry _registry = Substitute.For<IRunnerRegistry>();
    private readonly IRunnerRegistrationService _runners = Substitute.For<IRunnerRegistrationService>();
    private readonly TimeProvider _time = new FixedTimeProvider(Now);

    [Fact]
    public async Task ASilentRunner_IsRemoved()
    {
        var gone = Guid.NewGuid();
        this._registry.ListUnseenSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([gone]);
        this._runners.DeleteAsync(gone, Arg.Any<CancellationToken>()).Returns(RunnerDeletionOutcome.Deleted);

        var removed = await this.CreateWorker().SweepOnceAsync(TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(1, removed);
        await this._runners.Received(1).DeleteAsync(gone, Arg.Any<CancellationToken>());
    }

    // The cutoff is what the operator asked for, measured from now. Asserted because an off-by-one here
    // reaps a fleet that is merely idle.
    [Fact]
    public async Task TheCutoff_IsTheConfiguredWindowBeforeNow()
    {
        this._registry.ListUnseenSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await this.CreateWorker().SweepOnceAsync(TimeSpan.FromDays(7), CancellationToken.None);

        await this._registry.Received(1).ListUnseenSinceAsync(
            Now.AddDays(-7),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    // A silent runner that still holds a lease is one the control plane has not given up on. Tearing its
    // row out would turn housekeeping into an outage, so the sweep leaves it for the reclaim path.
    [Fact]
    public async Task ASilentRunnerStillHoldingALease_IsLeftAlone()
    {
        var busy = Guid.NewGuid();
        this._registry.ListUnseenSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([busy]);
        this._runners.DeleteAsync(busy, Arg.Any<CancellationToken>()).Returns(RunnerDeletionOutcome.HoldingLease);

        var removed = await this.CreateWorker().SweepOnceAsync(TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(0, removed);
    }

    // Housekeeping must not take the host down. A sweep that throws is logged and retried on the next tick.
    [Fact]
    public async Task AFailedSweep_ReportsNothingRemovedRatherThanThrowing()
    {
        this._registry.ListUnseenSinceAsync(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<Guid>>>(_ => throw new InvalidOperationException("the database went away"));

        var removed = await this.CreateWorker().SweepOnceAsync(TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal(0, removed);
    }

    /// <summary>A clock that does not move, so the cutoff a sweep computes is exactly assertable.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private RunnerRegistryPruneWorker CreateWorker()
    {
        var services = new ServiceCollection();
        services.AddSingleton(this._registry);
        services.AddSingleton(this._runners);

        return new RunnerRegistryPruneWorker(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            this._time,
            NullLogger<RunnerRegistryPruneWorker>.Instance);
    }
}
