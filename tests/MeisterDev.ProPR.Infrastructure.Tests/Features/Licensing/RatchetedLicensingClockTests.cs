// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Services;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

/// <summary>
///     Covers what the clock reports for a host clock ahead of and behind the recorded instant, when it writes,
///     and when it reports a host clock that has gone backwards.
/// </summary>
public sealed class RatchetedLicensingClockTests
{
    /// <summary>How many readings are released together at the flag that suppresses a repeated report.</summary>
    private const int RendezvousParticipants = 8;

    /// <summary>
    ///     How many times that release is repeated. One round can miss a race, so the assertion is made over
    ///     enough of them to reach a guard that is not atomic.
    /// </summary>
    private const int RendezvousRounds = 50;

    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AFirstReading_RecordsTheHostClockAndReportsIt()
    {
        var store = new InMemoryObservedTimeStore();
        var sut = CreateClock(store, new FakeTimeProvider(Now), []);

        var instant = await sut.GetUtcNowAsync();

        Assert.Equal(Now, instant);
        Assert.Equal(Now, store.Recorded);
        Assert.Equal(1, store.AdvanceCount);
    }

    // Normal operation: the host clock runs forward and the recorded instant follows it, so an installation
    // restarted at any point resumes from a value close to where it left off.
    [Fact]
    public async Task AHostClockRunningForward_RaisesTheRecordedInstant()
    {
        var store = new InMemoryObservedTimeStore();
        var timeProvider = new FakeTimeProvider(Now);
        var sut = CreateClock(store, timeProvider, []);

        await sut.GetUtcNowAsync();
        timeProvider.Advance(TimeSpan.FromHours(2));
        var second = await sut.GetUtcNowAsync();
        timeProvider.Advance(TimeSpan.FromHours(2));
        var third = await sut.GetUtcNowAsync();

        Assert.Equal(Now.AddHours(2), second);
        Assert.Equal(Now.AddHours(4), third);
        Assert.Equal(Now.AddHours(4), store.Recorded);
        Assert.Equal(3, store.AdvanceCount);
    }

    // The clock is read on every license-state load, which is about once a minute per replica. Writing each time
    // would put a row under constant update for no gain, because what the clock reports comes from the reading
    // and the row rather than from the write.
    [Fact]
    public async Task AReadingLessThanAnIntervalPastTheRecordedInstant_WritesNothing()
    {
        var store = new InMemoryObservedTimeStore(Now);
        var timeProvider = new FakeTimeProvider(Now.AddMinutes(59));
        var sut = CreateClock(store, timeProvider, []);

        var instant = await sut.GetUtcNowAsync();

        Assert.Equal(Now.AddMinutes(59), instant);
        Assert.Equal(Now, store.Recorded);
        Assert.Equal(0, store.AdvanceCount);
    }

    [Fact]
    public async Task AReadingAnIntervalPastTheRecordedInstant_WritesOnce()
    {
        var store = new InMemoryObservedTimeStore(Now);
        var timeProvider = new FakeTimeProvider(Now.AddHours(1));
        var sut = CreateClock(store, timeProvider, []);

        await sut.GetUtcNowAsync();
        await sut.GetUtcNowAsync();

        Assert.Equal(Now.AddHours(1), store.Recorded);
        Assert.Equal(1, store.AdvanceCount);
    }

    // A host clock set back reports the recorded value instead, so a license term judged against it does not
    // move.
    [Fact]
    public async Task AHostClockBehindTheRecordedInstant_ReportsTheRecordedInstant()
    {
        var store = new InMemoryObservedTimeStore(Now);
        var sut = CreateClock(store, new FakeTimeProvider(Now.AddYears(-1)), []);

        var instant = await sut.GetUtcNowAsync();

        Assert.Equal(Now, instant);
    }

    [Fact]
    public async Task AHostClockBehindTheRecordedInstant_WritesNothing()
    {
        var store = new InMemoryObservedTimeStore(Now);
        var sut = CreateClock(store, new FakeTimeProvider(Now.AddYears(-1)), []);

        await sut.GetUtcNowAsync();

        Assert.Equal(Now, store.Recorded);
        Assert.Equal(0, store.AdvanceCount);
    }

    // Time synchronisation moves a host clock by small amounts as a matter of course, and an operator has
    // nothing to do about it.
    [Fact]
    public async Task ASmallBackwardsStep_IsNotReported()
    {
        var warnings = new List<string>();
        var store = new InMemoryObservedTimeStore(Now);
        var sut = CreateClock(store, new FakeTimeProvider(Now.AddMinutes(-4)), warnings);

        var instant = await sut.GetUtcNowAsync();

        Assert.Equal(Now, instant);
        Assert.Empty(warnings);
    }

    // The condition holds for as long as the host clock stays behind, and the clock is read on every
    // license-state load, so reporting each reading would fill the log with one repeated line.
    [Fact]
    public async Task ALargeBackwardsStep_IsReportedOnceAcrossRepeatedReadings()
    {
        var warnings = new List<string>();
        var store = new InMemoryObservedTimeStore(Now);
        var sut = CreateClock(store, new FakeTimeProvider(Now.AddHours(-6)), warnings);

        await sut.GetUtcNowAsync();
        await sut.GetUtcNowAsync();
        await sut.GetUtcNowAsync();

        Assert.Single(warnings);
        Assert.Contains("behind", warnings[0], StringComparison.Ordinal);
    }

    // Readings arriving together share one flag, and each of them sees the same host clock behind the same
    // recorded instant, so exactly one of them may claim the report. The store holds every reading until they
    // have all arrived and releases them together, which is what puts them at the flag at the same time. The
    // rounds repeat because a guard that reads the flag before writing it loses the race only some of the time.
    [Fact]
    public async Task ConcurrentReadingsBehindTheRecordedInstant_AreReportedOnce()
    {
        for (var round = 0; round < RendezvousRounds; round++)
        {
            var warnings = new List<string>();
            var store = new RendezvousObservedTimeStore(Now, RendezvousParticipants);
            var sut = CreateClock(store, new FakeTimeProvider(Now.AddHours(-6)), warnings);

            var readings = Enumerable.Range(0, RendezvousParticipants)
                .Select(_ => Task.Run(() => sut.GetUtcNowAsync()))
                .ToList();

            await store.AllArrived;
            store.Release();
            await Task.WhenAll(readings);

            Assert.Single(warnings);
        }
    }

    // A required advance makes the host reading durable before it is used for a license decision. If the write
    // fails, the next decision cannot safely use an instant that would disappear on restart.
    [Fact]
    public async Task AnIntervalAdvanceThatFails_StopsTheReading()
    {
        var warnings = new List<string>();
        var store = new InMemoryObservedTimeStore(Now)
        {
            AdvanceFailure = new InvalidOperationException("the row could not be written"),
        };
        var sut = CreateClock(store, new FakeTimeProvider(Now.AddHours(2)), warnings);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetUtcNowAsync());

        Assert.Equal(Now, store.Recorded);
        Assert.Single(warnings);
        Assert.Contains("could not be recorded", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInitialAdvanceThatFails_StopsTheReading()
    {
        var warnings = new List<string>();
        var store = new InMemoryObservedTimeStore
        {
            AdvanceFailure = new InvalidOperationException("the row could not be written"),
        };
        var sut = CreateClock(store, new FakeTimeProvider(Now), warnings);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetUtcNowAsync());

        Assert.Null(store.Recorded);
        Assert.Single(warnings);
    }

    [Fact]
    public async Task ACancelledRequiredAdvance_PropagatesCancellationWithoutLogging()
    {
        var warnings = new List<string>();
        var store = new InMemoryObservedTimeStore
        {
            AdvanceFailure = new OperationCanceledException(),
        };
        var sut = CreateClock(store, new FakeTimeProvider(Now), warnings);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetUtcNowAsync());

        Assert.Empty(warnings);
    }

    // A write that failed recorded nothing, so the next reading has to attempt it again. Suppressing the
    // attempt for an interval would let every reading in that window return an instant a restart would lose,
    // which is the case a required advance exists to prevent.
    [Fact]
    public async Task AnAdvanceThatFailed_DoesNotSuppressTheNextOne()
    {
        var store = new InMemoryObservedTimeStore
        {
            AdvanceFailure = new InvalidOperationException("the row could not be written"),
            FailuresBeforeSuccess = 1,
        };
        var sut = CreateClock(store, new FakeTimeProvider(Now), []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetUtcNowAsync());
        var instant = await sut.GetUtcNowAsync();

        Assert.Equal(Now, instant);
        Assert.Equal(Now, store.Recorded);
        Assert.Equal(1, store.AdvanceCount);
    }

    // Readings that arrive together produce one write, and none of them returns until it has persisted. The
    // store holds the write open, so a reading that returned while it was held would be answering from an
    // instant the row does not yet carry.
    [Fact]
    public async Task ConcurrentFirstReadings_WriteOnceAndNoneReturnsBeforeItPersisted()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryObservedTimeStore { BeforeAdvance = () => released.Task };
        var sut = CreateClock(store, new FakeTimeProvider(Now), []);

        var readings = Enumerable.Range(0, RendezvousParticipants)
            .Select(_ => Task.Run(() => sut.GetUtcNowAsync()))
            .ToList();

        await Task.WhenAny(Task.WhenAll(readings), Task.Delay(TimeSpan.FromMilliseconds(250)));
        Assert.All(readings, reading => Assert.False(reading.IsCompleted));

        released.SetResult();
        var instants = await Task.WhenAll(readings);

        Assert.Equal(1, store.AdvanceCount);
        Assert.Equal(Now, store.Recorded);
        Assert.All(instants, instant => Assert.Equal(Now, instant));
    }

    // Another replica can raise the row between this reading's read and its write. What the write reports is
    // what the installation holds, and a reading answering from its own earlier read would evaluate a term
    // against an instant below the recorded floor.
    [Fact]
    public async Task AnAdvanceThatFindsAHigherRecordedInstant_ReportsThatInstant()
    {
        var store = new InMemoryObservedTimeStore(Now) { RaisedByAnotherReplica = Now.AddHours(5) };
        var sut = CreateClock(store, new FakeTimeProvider(Now.AddHours(2)), []);

        var instant = await sut.GetUtcNowAsync();

        Assert.Equal(Now.AddHours(5), instant);
    }

    // Two readings read the row at different moments, so the one that read first can finish last. Licensing
    // time must not move backwards between two decisions in one process, whatever order their reads land in.
    [Fact]
    public async Task AReadingThatFindsALoweredRow_DoesNotReportBelowWhatWasAlreadyReported()
    {
        var store = new InMemoryObservedTimeStore(Now.AddHours(5));
        var sut = CreateClock(store, new FakeTimeProvider(Now), []);

        var first = await sut.GetUtcNowAsync();
        store.Lower(Now);
        var second = await sut.GetUtcNowAsync();

        Assert.Equal(Now.AddHours(5), first);
        Assert.Equal(Now.AddHours(5), second);
    }

    // The recorded instant only rises, so a reading from a badly wrong clock would otherwise be permanent:
    // every replica would judge terms at it, and a license with years left would read as expired. A reading
    // further ahead than the recorded timeline allows is bounded and the condition is reported.
    [Fact]
    public async Task AHostClockFarAheadOfTheRecordedInstant_IsBoundedAndReported()
    {
        var warnings = new List<string>();
        var store = new InMemoryObservedTimeStore(Now);
        var sut = CreateClock(store, new FakeTimeProvider(Now.AddYears(5)), warnings);

        await sut.GetUtcNowAsync();

        Assert.Equal(Now + RatchetedLicensingClock.ForwardAllowance, store.Recorded);
        Assert.Single(warnings);
    }

    // Bounding one reading is not enough on its own: readings happen whenever the license state is loaded, so
    // a bound measured per reading would let a wrong clock carry the record forward again on each of them. No
    // time passes between these readings, so none of them may move the record.
    [Fact]
    public async Task RepeatedReadingsFromAClockFarAhead_DoNotCarryTheRecordFurther()
    {
        var store = new InMemoryObservedTimeStore(Now);
        var timeProvider = new MovableTimeProvider(Now.AddYears(5));
        var sut = CreateClock(store, timeProvider, []);

        await sut.GetUtcNowAsync();
        var afterFirst = store.Recorded;

        for (var reading = 0; reading < 20; reading++)
        {
            await sut.GetUtcNowAsync();
        }

        Assert.Equal(Now + RatchetedLicensingClock.ForwardAllowance, afterFirst);
        Assert.Equal(afterFirst, store.Recorded);
    }

    // A host that is ahead is not held back for good. The record is held to the time this process has observed
    // passing, so the installation catches up over its own uptime.
    [Fact]
    public async Task AHostClockAheadOfTheRecordedInstant_CatchesUpAsTimePasses()
    {
        var store = new InMemoryObservedTimeStore(Now);
        var timeProvider = new FakeTimeProvider(Now.AddYears(5));
        var sut = CreateClock(store, timeProvider, []);

        await sut.GetUtcNowAsync();
        var afterFirst = store.Recorded;

        timeProvider.Advance(TimeSpan.FromDays(2));
        await sut.GetUtcNowAsync();

        Assert.NotNull(afterFirst);
        Assert.True(store.Recorded > afterFirst);
        Assert.True(store.Recorded <= afterFirst.Value.AddDays(2) + RatchetedLicensingClock.ForwardAllowance);
    }

    // A first reading has nothing to be measured against, so it establishes the starting point as it stands.
    [Fact]
    public async Task AFirstReadingFarFromZero_IsRecordedWithoutBeingBounded()
    {
        var store = new InMemoryObservedTimeStore();
        var sut = CreateClock(store, new FakeTimeProvider(Now), []);

        await sut.GetUtcNowAsync();

        Assert.Equal(Now, store.Recorded);
    }

    // A host clock corrected and set back again is a new event for the operator, so the report is not suppressed
    // for the rest of the process's life.
    [Fact]
    public async Task AHostClockThatIsCorrectedAndSetBackAgain_IsReportedAgain()
    {
        var warnings = new List<string>();
        var store = new InMemoryObservedTimeStore(Now);
        var timeProvider = new MovableTimeProvider(Now.AddHours(-6));
        var sut = CreateClock(store, timeProvider, warnings);

        await sut.GetUtcNowAsync();
        timeProvider.MoveTo(Now);
        await sut.GetUtcNowAsync();
        timeProvider.MoveTo(Now.AddHours(-6));
        await sut.GetUtcNowAsync();

        Assert.Equal(2, warnings.Count);
    }

    private static RatchetedLicensingClock CreateClock(
        IHighestObservedTimeStore store,
        TimeProvider timeProvider,
        List<string> warnings)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => store);

        return new RatchetedLicensingClock(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            new CapturingLogger<RatchetedLicensingClock>(warnings));
    }

    /// <summary>
    ///     A store that keeps the recorded instant in memory and counts the writes it received, which is how
    ///     write suppression is told apart from a write that happened to record the same value.
    /// </summary>
    internal sealed class InMemoryObservedTimeStore(DateTimeOffset? recorded = null) : IHighestObservedTimeStore
    {
        public int AdvanceCount { get; private set; }

        public DateTimeOffset? Recorded { get; private set; } = recorded;

        /// <summary>Set to make the write fail, which stands in for a database that cannot take it.</summary>
        public Exception? AdvanceFailure { get; set; }

        /// <summary>
        ///     How many writes fail before the store starts taking them, which is how a store that recovers is
        ///     told apart from one that is down for good.
        /// </summary>
        public int FailuresBeforeSuccess { get; set; }

        /// <summary>
        ///     Set to the instant another replica has already raised the row to. The write keeps the greater of
        ///     the two values, so the reading that arrives with a lower one is answered with this.
        /// </summary>
        public DateTimeOffset? RaisedByAnotherReplica { get; set; }

        /// <summary>Runs before each write, which is where a test releases or blocks a concurrent reading.</summary>
        public Func<Task>? BeforeAdvance { get; set; }

        public Task<DateTimeOffset?> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(this.Recorded);

        /// <summary>
        ///     Sets the recorded instant without going through a write, which stands in for the row being
        ///     changed outside the process.
        /// </summary>
        public void Lower(DateTimeOffset instant) => this.Recorded = instant;

        public async Task<DateTimeOffset> AdvanceToAsync(
            DateTimeOffset instant,
            CancellationToken cancellationToken = default)
        {
            if (this.BeforeAdvance is { } beforeAdvance)
            {
                await beforeAdvance();
            }

            if (this.AdvanceFailure is { } failure)
            {
                if (this.FailuresBeforeSuccess == 0)
                {
                    throw failure;
                }

                this.FailuresBeforeSuccess--;

                if (this.FailuresBeforeSuccess == 0)
                {
                    this.AdvanceFailure = null;
                }

                throw failure;
            }

            this.AdvanceCount++;

            if (this.RaisedByAnotherReplica is { } raised && (this.Recorded is null || raised > this.Recorded.Value))
            {
                this.Recorded = raised;
            }

            if (this.Recorded is null || instant > this.Recorded.Value)
            {
                this.Recorded = instant;
            }

            return this.Recorded.Value;
        }
    }

    /// <summary>
    ///     A store that holds every reading until they have all arrived, then releases them on the test's signal.
    ///     Releasing from outside keeps any one reading from going on ahead of the others, which is what puts them
    ///     all at the report flag together.
    /// </summary>
    private sealed class RendezvousObservedTimeStore(DateTimeOffset recorded, int participants)
        : IHighestObservedTimeStore
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _arrived;

        /// <summary>Completes once every reading has reached the store.</summary>
        public Task AllArrived => this._allArrived.Task;

        /// <summary>Lets every waiting reading continue.</summary>
        public void Release() => this._released.SetResult();

        public async Task<DateTimeOffset?> GetAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref this._arrived) == participants)
            {
                this._allArrived.SetResult();
            }

            await this._released.Task;

            return recorded;
        }

        public Task<DateTimeOffset> AdvanceToAsync(
            DateTimeOffset instant,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(instant > recorded ? instant : recorded);
    }

    /// <summary>
    ///     A host clock a test moves in either direction. <see cref="FakeTimeProvider" /> refuses to be set to an
    ///     earlier instant, which is the case this clock exists to cover.
    /// </summary>
    private sealed class MovableTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        private DateTimeOffset _instant = instant;

        public override DateTimeOffset GetUtcNow() => this._instant;

        public void MoveTo(DateTimeOffset instant) => this._instant = instant;
    }

    /// <summary>Keeps the rendered warnings, which is what the once-per-transition rule is asserted against.</summary>
    internal sealed class CapturingLogger<T>(List<string> warnings) : ILogger<T>
    {
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

            if (logLevel < LogLevel.Warning)
            {
                return;
            }

            // Readings can arrive together, so the list is guarded here rather than at every call site.
            lock (warnings)
            {
                warnings.Add(formatter(state, exception));
            }
        }
    }
}

/// <summary>
///     The clock over the production store. The in-memory store cannot show that the recorded instant survives a
///     new connection, which is what makes the value resistant to a restart.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class RatchetedLicensingClockPostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return this.ResetTablesAsync();
    }

    public Task DisposeAsync() => this.ResetTablesAsync();

    [Fact]
    public async Task AHostClockRunningForward_RaisesTheStoredInstant()
    {
        var timeProvider = new FakeTimeProvider(Now);
        await using var host = this.CreateHost();
        var sut = host.CreateClock(timeProvider);

        await sut.GetUtcNowAsync();
        timeProvider.Advance(TimeSpan.FromHours(2));
        var second = await sut.GetUtcNowAsync();

        Assert.Equal(Now.AddHours(2), second);
        Assert.Equal(Now.AddHours(2), await host.ReadStoredInstantAsync());
    }

    // A new process reads the stored value rather than starting from its own host clock, so a restart does not
    // clear the floor a rollback has to clear.
    [Fact]
    public async Task AProcessStartingWithTheHostClockBehindTheStoredInstant_ReportsTheStoredInstant()
    {
        await using var host = this.CreateHost();

        await host.CreateClock(new FakeTimeProvider(Now)).GetUtcNowAsync();

        var afterRestart = await host.CreateClock(new FakeTimeProvider(Now.AddYears(-1))).GetUtcNowAsync();

        Assert.Equal(Now, afterRestart);
        Assert.Equal(Now, await host.ReadStoredInstantAsync());
    }

    private async Task ResetTablesAsync()
    {
        await using var db = this.CreateContext();
        await db.InstallationObservedTime.ExecuteDeleteAsync();
    }

    private MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private ClockHost CreateHost()
    {
        var services = new ServiceCollection();
        services.AddDbContext<MeisterProPRDbContext>(options => options
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector()));
        services.AddScoped<IHighestObservedTimeStore, InstallationObservedTimeRepository>();

        return new ClockHost(services.BuildServiceProvider());
    }

    /// <summary>A service provider over the production store, and the clocks built on top of it.</summary>
    private sealed class ClockHost(ServiceProvider serviceProvider) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => serviceProvider.DisposeAsync();

        public RatchetedLicensingClock CreateClock(TimeProvider timeProvider) => new(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            new RatchetedLicensingClockTests.CapturingLogger<RatchetedLicensingClock>([]));

        public async Task<DateTimeOffset?> ReadStoredInstantAsync()
        {
            using var scope = serviceProvider.CreateScope();

            return await scope.ServiceProvider.GetRequiredService<IHighestObservedTimeStore>().GetAsync();
        }
    }
}
