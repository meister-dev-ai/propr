// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Api.Workers;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Api.Tests.Workers;

/// <summary>
///     Reporting the installation crossing a license lifecycle boundary. The sweep exists so an installation
///     nobody is using still records its expiry in the log; capability resolution derives the stage for itself,
///     so nothing here decides what is available.
/// </summary>
public sealed class LicenseStageWorkerTests
{
    private static readonly DateTimeOffset IdentityCreatedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task AStageChange_IsReportedOnce()
    {
        var stateProvider = new StubLicenseStateProvider(StateIn(LicenseStage.Active));
        var log = new ListLogger();
        var worker = CreateWorker(stateProvider, log);

        await worker.SweepOnceAsync(CancellationToken.None);
        stateProvider.State = StateIn(LicenseStage.Warning);
        await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, log.Entries.Count);
        Assert.Contains(nameof(LicenseStage.Warning), log.Entries[1].Message, StringComparison.Ordinal);
    }

    // The stage a licensed installation sits in holds for months, and the sweep runs every quarter of an hour,
    // so reporting each sweep would fill the log with one repeated line.
    [Fact]
    public async Task AStageThatDoesNotChange_IsReportedOnceAcrossRepeatedSweeps()
    {
        var stateProvider = new StubLicenseStateProvider(StateIn(LicenseStage.Grace));
        var log = new ListLogger();
        var worker = CreateWorker(stateProvider, log);

        await worker.SweepOnceAsync(CancellationToken.None);
        await worker.SweepOnceAsync(CancellationToken.None);
        await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Single(log.Entries);
    }

    // Renewing during grace moves the installation back to a running term, which is as much a transition as the
    // one that put it there.
    [Fact]
    public async Task AStageThatReturnsToWhereItWas_IsReportedAgain()
    {
        var stateProvider = new StubLicenseStateProvider(StateIn(LicenseStage.Active));
        var log = new ListLogger();
        var worker = CreateWorker(stateProvider, log);

        await worker.SweepOnceAsync(CancellationToken.None);
        stateProvider.State = StateIn(LicenseStage.Grace);
        await worker.SweepOnceAsync(CancellationToken.None);
        stateProvider.State = StateIn(LicenseStage.Active);
        await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(3, log.Entries.Count);
    }

    // The stages an operator has to act on are reported as warnings, so an expiry is visible at a log level a
    // deployment is likely to be collecting. The level does not depend on whether the stage was seen before.
    [Theory]
    [InlineData(LicenseStage.Warning, LogLevel.Warning)]
    [InlineData(LicenseStage.Grace, LogLevel.Warning)]
    [InlineData(LicenseStage.Reverted, LogLevel.Warning)]
    [InlineData(LicenseStage.Active, LogLevel.Information)]
    [InlineData(LicenseStage.None, LogLevel.Information)]
    public async Task TheReportedLevel_FollowsWhetherTheStageNeedsAnOperator(LicenseStage stage, LogLevel expectedLevel)
    {
        var firstObservationLog = new ListLogger();
        await CreateWorker(new StubLicenseStateProvider(StateIn(stage)), firstObservationLog)
            .SweepOnceAsync(CancellationToken.None);

        // The same stage reached from another one, so the level does not turn on whether it was seen before.
        var stateProvider = new StubLicenseStateProvider(StateIn(LicenseStage.NotYetValid));
        var transitionLog = new ListLogger();
        var worker = CreateWorker(stateProvider, transitionLog);
        await worker.SweepOnceAsync(CancellationToken.None);
        stateProvider.State = StateIn(stage);
        await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(expectedLevel, Assert.Single(firstObservationLog.Entries).LogLevel);
        Assert.Equal(expectedLevel, transitionLog.Entries[1].LogLevel);
    }

    // The first sweep of a process has no previous stage to name. A message worded as a transition would leave an
    // empty value where the previous stage goes and report a change nothing observed.
    [Fact]
    public async Task TheFirstObservationOfABenignStage_IsReportedWithoutAPreviousStage()
    {
        var log = new ListLogger();
        var worker = CreateWorker(new StubLicenseStateProvider(StateIn(LicenseStage.Active)), log);

        await worker.SweepOnceAsync(CancellationToken.None);

        var entry = Assert.Single(log.Entries);
        Assert.Equal("The license stage is Active.", entry.Message);
        Assert.DoesNotContain("changed from", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALaterTransitionIntoABenignStage_NamesTheStageItCameFrom()
    {
        var stateProvider = new StubLicenseStateProvider(StateIn(LicenseStage.Grace));
        var log = new ListLogger();
        var worker = CreateWorker(stateProvider, log);

        await worker.SweepOnceAsync(CancellationToken.None);
        stateProvider.State = StateIn(LicenseStage.Active);
        await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Equal("The license stage changed from Grace to Active.", log.Entries[1].Message);
    }

    // The floor keeps a mistyped value from turning the log line into a poll, and zero is the way an operator
    // silences it rather than a value to clamp.
    [Theory]
    [InlineData(null, 900)]
    [InlineData(3600, 3600)]
    [InlineData(60, 60)]
    [InlineData(30, 60)]
    [InlineData(1, 60)]
    public void TheInterval_DefaultsToFifteenMinutesAndIsFlooredAtAMinute(int? configured, int expectedSeconds)
    {
        var interval = LicenseStageWorker.ResolveInterval(ConfigurationWith(configured));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), interval);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveInterval_SwitchesTheReportingOff(int configured)
    {
        Assert.Null(LicenseStageWorker.ResolveInterval(ConfigurationWith(configured)));
    }

    // The sweep reads the state through the provider and leaves its cache alone, so it neither forces a load nor
    // holds one back. What keeps the ratchet moving is that the interval is longer than the cache window, which
    // makes an idle installation's sweep a load in its own right.
    [Fact]
    public async Task TheSweep_ReadsTheStateWithoutDiscardingTheCachedOne()
    {
        var stateProvider = new StubLicenseStateProvider(StateIn(LicenseStage.Active));
        var worker = CreateWorker(stateProvider, new ListLogger());

        await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(1, stateProvider.ReadCount);
        Assert.Equal(0, stateProvider.InvalidateCount);
    }

    [Fact]
    public async Task TheSweep_ReturnsTheStageItObserved()
    {
        var worker = CreateWorker(new StubLicenseStateProvider(StateIn(LicenseStage.Reverted)), new ListLogger());

        Assert.Equal(LicenseStage.Reverted, await worker.SweepOnceAsync(CancellationToken.None));
    }

    // An installation nobody is using still has to record a move it has made, which is what makes the sweep the
    // place the profile is re-observed from.
    [Fact]
    public async Task TheSweep_ReObservesTheSystemProfileAgainstTheInstallationsFirstSeenInstant()
    {
        var identityStore = new StubLicensingIdentityStore(IdentityCreatedAt);
        var profileObserver = new RecordingSystemProfileObserver();
        var worker = CreateWorker(
            new StubLicenseStateProvider(StateIn(LicenseStage.Active)),
            new ListLogger(),
            identityStore,
            profileObserver);

        await worker.SweepOnceAsync(CancellationToken.None);
        await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Equal([IdentityCreatedAt, IdentityCreatedAt], profileObserver.Observations);
    }

    // The profile records the instant the installation was first seen, so there is nothing to record it against
    // before the identity exists.
    [Fact]
    public async Task TheSweep_ObservesNoProfileBeforeTheInstallationHasAnIdentity()
    {
        var profileObserver = new RecordingSystemProfileObserver();
        var worker = CreateWorker(
            new StubLicenseStateProvider(StateIn(LicenseStage.Active)),
            new ListLogger(),
            new StubLicensingIdentityStore(null),
            profileObserver);

        await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Empty(profileObserver.Observations);
    }

    // The profile decides nothing, so a failure there is reported on its own and must not cost the sweep the
    // stage it read.
    [Fact]
    public async Task AFailedObservation_IsReportedSeparatelyAndLeavesTheStageAlone()
    {
        var log = new ListLogger();
        var worker = CreateWorker(
            new StubLicenseStateProvider(StateIn(LicenseStage.Active)),
            log,
            new ThrowingLicensingIdentityStore(),
            new RecordingSystemProfileObserver());

        Assert.Equal(LicenseStage.Active, await worker.SweepOnceAsync(CancellationToken.None));
        Assert.Equal("The license stage is Active.", log.Entries[0].Message);
        Assert.Equal(LogLevel.Warning, log.Entries[1].LogLevel);
        Assert.Contains("system profile", log.Entries[1].Message, StringComparison.Ordinal);
    }

    // The allowance is metered on the same cadence as the lifecycle, so an installation nobody is using still
    // records and reports a month whose authors go above the licensed number.
    [Fact]
    public async Task TheSweep_EvaluatesTheAuthorAllowance()
    {
        var evaluator = new RecordingAuthorOverageEvaluator();
        var worker = CreateWorker(
            new StubLicenseStateProvider(StateIn(LicenseStage.Active)),
            new ListLogger(),
            overageEvaluator: evaluator);

        await worker.SweepOnceAsync(CancellationToken.None);
        await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, evaluator.Evaluations);
    }

    // The sweep has no count of its own to hand over, so the evaluation counts the month for itself.
    [Fact]
    public async Task TheSweep_LeavesTheAuthorCountToTheEvaluation()
    {
        var evaluator = new RecordingAuthorOverageEvaluator();
        var worker = CreateWorker(
            new StubLicenseStateProvider(StateIn(LicenseStage.Active)),
            new ListLogger(),
            overageEvaluator: evaluator);

        await worker.SweepOnceAsync(CancellationToken.None);

        Assert.Equal([null], evaluator.StatedCounts);
    }

    // The evaluation records and reports and decides nothing, so a failure there is reported on its own and must
    // not cost the sweep the stage it read.
    [Fact]
    public async Task AFailedAllowanceEvaluation_IsReportedSeparatelyAndLeavesTheStageAlone()
    {
        var log = new ListLogger();
        var worker = CreateWorker(
            new StubLicenseStateProvider(StateIn(LicenseStage.Active)),
            log,
            overageEvaluator: new ThrowingAuthorOverageEvaluator());

        Assert.Equal(LicenseStage.Active, await worker.SweepOnceAsync(CancellationToken.None));
        Assert.Equal("The license stage is Active.", log.Entries[0].Message);
        Assert.Equal(LogLevel.Warning, log.Entries[1].LogLevel);
        Assert.Contains("author allowance", log.Entries[1].Message, StringComparison.Ordinal);
        Assert.Contains("Nothing has been withheld", log.Entries[1].Message, StringComparison.Ordinal);
    }

    // A deployment without a database registers no license state provider, so the sweep has nothing to read and
    // must not treat that as a fault.
    [Fact]
    public async Task WithoutTheLicensingModule_TheSweepReportsNothing()
    {
        var log = new ListLogger();
        var worker = new LicenseStageWorker(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            log);

        Assert.Null(await worker.SweepOnceAsync(CancellationToken.None));
        Assert.Empty(log.Entries);
    }

    // Reporting must not take the host down. A sweep that throws is logged and retried on the next tick.
    [Fact]
    public async Task AFailedSweep_ReportsNoStageRatherThanThrowing()
    {
        var log = new ListLogger();
        var worker = CreateWorker(new ThrowingLicenseStateProvider(), log);

        Assert.Null(await worker.SweepOnceAsync(CancellationToken.None));
        Assert.Equal(LogLevel.Warning, Assert.Single(log.Entries).LogLevel);
    }

    private static LicenseState StateIn(LicenseStage stage) => new()
    {
        Kind = stage == LicenseStage.None ? LicenseStateKind.None : LicenseStateKind.Verified,
        Stage = stage,
    };

    private static LicenseStageWorker CreateWorker(
        ILicenseStateProvider stateProvider,
        ILogger<LicenseStageWorker> logger,
        ILicensingIdentityStore? identityStore = null,
        ISystemProfileObserver? profileObserver = null,
        IAuthorOverageEvaluator? overageEvaluator = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(stateProvider);

        if (identityStore is not null)
        {
            services.AddSingleton(identityStore);
        }

        if (profileObserver is not null)
        {
            services.AddSingleton(profileObserver);
        }

        if (overageEvaluator is not null)
        {
            services.AddSingleton(overageEvaluator);
        }

        return new LicenseStageWorker(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            logger);
    }

    /// <summary>Reports one instant as the installation's first-seen one, or none when it has no identity yet.</summary>
    private sealed class StubLicensingIdentityStore(DateTimeOffset? createdAt) : ILicensingIdentityStore
    {
        public Task<Guid> GetOrCreateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.Parse("6f2ad2c1-3b45-4a2f-8c1a-6b0e5d9f1a23"));

        public Task<DateTimeOffset?> GetCreatedAtAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(createdAt);
    }

    private sealed class ThrowingLicensingIdentityStore : ILicensingIdentityStore
    {
        public Task<Guid> GetOrCreateAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the database went away");

        public Task<DateTimeOffset?> GetCreatedAtAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the database went away");
    }

    /// <summary>Keeps the instant each observation was asked for.</summary>
    private sealed class RecordingSystemProfileObserver : ISystemProfileObserver
    {
        public List<DateTimeOffset> Observations { get; } = [];

        public Task ObserveAsync(DateTimeOffset identityCreatedAt, CancellationToken cancellationToken = default)
        {
            this.Observations.Add(identityCreatedAt);

            return Task.CompletedTask;
        }
    }

    /// <summary>Keeps how often the allowance was evaluated and what count each call was given.</summary>
    private sealed class RecordingAuthorOverageEvaluator : IAuthorOverageEvaluator
    {
        public int Evaluations => this.StatedCounts.Count;

        public List<AuthorMonthCount?> StatedCounts { get; } = [];

        public Task<AuthorOverageState?> EvaluateAsync(
            AuthorMonthCount? observedAuthors = null,
            CancellationToken cancellationToken = default)
        {
            this.StatedCounts.Add(observedAuthors);

            return Task.FromResult<AuthorOverageState?>(null);
        }
    }

    private sealed class ThrowingAuthorOverageEvaluator : IAuthorOverageEvaluator
    {
        public Task<AuthorOverageState?> EvaluateAsync(
            AuthorMonthCount? observedAuthors = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the database went away");
    }

    /// <summary>Configuration carrying the interval variable, or none when the caller passes no value.</summary>
    private static IConfiguration ConfigurationWith(int? intervalSeconds)
    {
        var builder = new ConfigurationBuilder();

        if (intervalSeconds is { } configured)
        {
            builder.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["LICENSE_STAGE_INTERVAL_SECONDS"] = configured.ToString(CultureInfo.InvariantCulture),
                });
        }

        return builder.Build();
    }

    /// <summary>A provider whose state a test moves, counting the reads and the invalidations it received.</summary>
    private sealed class StubLicenseStateProvider(LicenseState state) : ILicenseStateProvider
    {
        public int InvalidateCount { get; private set; }

        public int ReadCount { get; private set; }

        public LicenseState State { get; set; } = state;

        public Task<LicenseState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            this.ReadCount++;

            return Task.FromResult(this.State);
        }

        public void Invalidate() => this.InvalidateCount++;
    }

    private sealed class ThrowingLicenseStateProvider : ILicenseStateProvider
    {
        public Task<LicenseState> GetStateAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the database went away");

        public void Invalidate()
        {
        }
    }

    /// <summary>Keeps what was logged, which is what the once-per-transition rule is asserted against.</summary>
    private sealed class ListLogger : ILogger<LicenseStageWorker>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            this.Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }

        public sealed record LogEntry(LogLevel LogLevel, string Message);
    }
}
