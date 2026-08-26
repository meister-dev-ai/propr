// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

/// <summary>
///     The record of the months whose counted authors went above the licensed number. The two counts only rise
///     and the ratchet is in the update statement, so these run against PostgreSQL.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class AuthorOverageRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly DateOnly August = new(2026, 8, 1);

    private static readonly DateOnly September = new(2026, 9, 1);

    private static readonly DateTimeOffset EarlyInAugust = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset LaterInAugust = new(2026, 8, 19, 17, 30, 0, TimeSpan.Zero);

    /// <summary>A few minutes after the first observation, which is inside the interval that guards the write.</summary>
    private static readonly DateTimeOffset MinutesAfterEarlyInAugust = EarlyInAugust.AddMinutes(7);

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return this.ResetTableAsync();
    }

    public Task DisposeAsync()
    {
        return this.ResetTableAsync();
    }

    // The first observation writes both counts and both instants, and says it was the first, which is the edge
    // the once-per-month report is taken from.
    [Fact]
    public async Task TheFirstObservationOfAMonth_InsertsBothCountsAndReportsItself()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorOverageRepository(db);

        var first = await sut.RecordAsync(August, 5, 8, EarlyInAugust);

        Assert.True(first);

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.Equal(new DateOnly(2026, 8, 1), recorded.OverageMonth);
        Assert.Equal(5, recorded.LicensedCount);
        Assert.Equal(8, recorded.HighestObservedCount);
        Assert.Equal(EarlyInAugust, recorded.FirstObservedAt);
        Assert.Equal(EarlyInAugust, recorded.LastObservedAt);
    }

    [Fact]
    public async Task AHigherCountLaterInTheMonth_RaisesTheHighestCountAndMovesTheLastObservedInstant()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorOverageRepository(db);

        await sut.RecordAsync(August, 5, 8, EarlyInAugust);
        var later = await sut.RecordAsync(August, 5, 13, LaterInAugust);

        Assert.False(later);

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.Equal(13, recorded.HighestObservedCount);
        Assert.Equal(EarlyInAugust, recorded.FirstObservedAt);
        Assert.Equal(LaterInAugust, recorded.LastObservedAt);
    }

    // The highest count the month reached is what the record states, so an evaluation reading fewer authors than
    // an earlier one moves the last-observed instant alone.
    [Fact]
    public async Task ALowerCountLaterInTheMonth_MovesTheLastObservedInstantOnly()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorOverageRepository(db);

        await sut.RecordAsync(August, 5, 13, EarlyInAugust);

        // False, because the month already has a row. Only the first observation reports itself, and that is
        // what the once-per-month log line is taken from.
        Assert.False(await sut.RecordAsync(August, 5, 9, LaterInAugust));

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.Equal(13, recorded.HighestObservedCount);
        Assert.Equal(LaterInAugust, recorded.LastObservedAt);
    }

    // A month above the number is observed on every lifecycle sweep and every administration read. An
    // observation that raises nothing and arrives inside the interval writes no row version at all, so the
    // last-observed instant stays where the previous write left it.
    [Fact]
    public async Task AnUnchangedCountWithinTheInterval_LeavesTheRowExactlyAsItWas()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorOverageRepository(db);

        await sut.RecordAsync(August, 5, 8, EarlyInAugust);
        Assert.False(await sut.RecordAsync(August, 5, 8, MinutesAfterEarlyInAugust));

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.Equal(8, recorded.HighestObservedCount);
        Assert.Equal(EarlyInAugust, recorded.FirstObservedAt);
        Assert.Equal(EarlyInAugust, recorded.LastObservedAt);
    }

    // A count that rises is written whenever it rises. The interval guards an observation that would store the
    // values the row already holds, not one that carries a higher number.
    [Fact]
    public async Task AHigherCountWithinTheInterval_IsWrittenAnyway()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorOverageRepository(db);

        await sut.RecordAsync(August, 5, 8, EarlyInAugust);
        await sut.RecordAsync(August, 5, 9, MinutesAfterEarlyInAugust);

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.Equal(9, recorded.HighestObservedCount);
        Assert.Equal(MinutesAfterEarlyInAugust, recorded.LastObservedAt);
    }

    // The instant moves once an interval has passed, so the record still says roughly when the month was last
    // seen above the number.
    [Fact]
    public async Task AnUnchangedCountAnIntervalLater_MovesTheLastObservedInstant()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorOverageRepository(db);
        var anIntervalLater = EarlyInAugust + AuthorOverageRepository.ObservationInterval;

        await sut.RecordAsync(August, 5, 8, EarlyInAugust);
        await sut.RecordAsync(August, 5, 8, anIntervalLater);

        Assert.Equal(
            anIntervalLater,
            (await db.LicensingAuthorOverage.AsNoTracking().SingleAsync()).LastObservedAt);
    }

    // The number the license stated when the month first went above it is what the row keeps. A license replaced
    // mid-month does not change what was observed under the previous one.
    [Fact]
    public async Task TheLicensedNumber_StaysAtWhatTheFirstObservationRecorded()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorOverageRepository(db);

        await sut.RecordAsync(August, 5, 8, EarlyInAugust);
        await sut.RecordAsync(August, 40, 8, LaterInAugust);

        Assert.Equal(5, (await db.LicensingAuthorOverage.AsNoTracking().SingleAsync()).LicensedCount);
    }

    // Rows are kept as history. A recorded month and a later recorded month are two rows, and nothing removes
    // the earlier one.
    [Fact]
    public async Task TwoMonthsAboveTheNumber_AreTwoRows()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorOverageRepository(db);

        await sut.RecordAsync(August, 5, 8, EarlyInAugust);
        await sut.RecordAsync(September, 5, 6, new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero));

        var months = await db.LicensingAuthorOverage
            .AsNoTracking()
            .OrderBy(row => row.OverageMonth)
            .Select(row => row.OverageMonth)
            .ToListAsync();

        Assert.Equal([new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1)], months);
    }

    // The month an evaluation falls in turns over at midnight UTC. Two evaluations a couple of minutes apart
    // across that boundary belong to different months, and each is a first observation of its own month.
    [Fact]
    public async Task EvaluationsEitherSideOfMidnightUtc_AreTwoFirstObservations()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorOverageRepository(db);

        var lastMinuteOfAugust = new DateTimeOffset(2026, 8, 31, 23, 59, 0, TimeSpan.Zero);
        var firstMinuteOfSeptember = new DateTimeOffset(2026, 9, 1, 0, 1, 0, TimeSpan.Zero);

        Assert.True(await sut.RecordAsync(August, 5, 8, lastMinuteOfAugust));
        Assert.True(await sut.RecordAsync(September, 5, 8, firstMinuteOfSeptember));
    }

    // The row is written to the month the caller counted, not to the month the instant of the write falls in.
    // An evaluation that reads a count minutes before midnight UTC and writes it minutes after would otherwise
    // insert against the next month, which nobody counted, and the ratchet on the row keeps that number for
    // good.
    [Fact]
    public async Task AnObservationWrittenInTheNextMonth_IsRecordedAgainstTheMonthItCounted()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorOverageRepository(db);

        Assert.True(await sut.RecordAsync(August, 5, 8, new DateTimeOffset(2026, 9, 1, 0, 1, 0, TimeSpan.Zero)));

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.Equal(August, recorded.OverageMonth);
        Assert.Equal(8, recorded.HighestObservedCount);
    }

    // A month that is not the first day of a month names no row the record can hold, so it is refused rather
    // than rounded to one.
    [Fact]
    public async Task AMonthThatIsNotTheFirstOfAMonth_IsRefused()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorOverageRepository(db);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.RecordAsync(new DateOnly(2026, 8, 12), 5, 8, EarlyInAugust));
    }

    // An unstated instant takes the database clock, which is what orders the observations replicas make of one
    // month whatever their own clocks read.
    [Fact]
    public async Task AnUnstatedInstant_TakesTheObservationInstantFromTheDatabaseClock()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorOverageRepository(db);

        var before = await ReadDatabaseInstantAsync(db);
        Assert.True(await sut.RecordAsync(August, 5, 8));
        var after = await ReadDatabaseInstantAsync(db);

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.InRange(recorded.FirstObservedAt, before, after);
        Assert.Equal(recorded.FirstObservedAt, recorded.LastObservedAt);
    }

    // The instant an unstated observation is attributed to is read once and then carried by both writes, so the
    // row's first-observed and last-observed values come from one reading rather than from two the statements
    // took for themselves.
    [Fact]
    public async Task AnUnstatedInstant_IsReadOnceAndCarriedByBothWrites()
    {
        var statements = new List<string>();
        await using var db = this.CreateLoggingContext(statements.Add);
        var sut = new AuthorOverageRepository(db);

        await sut.RecordAsync(August, 5, 8);
        statements.Clear();
        Assert.False(await sut.RecordAsync(August, 5, 13));

        var writes = statements
            .Where(statement => statement.Contains("licensing_author_overage", StringComparison.Ordinal))
            .ToList();

        // The insert that conflicts and the update that follows it, neither of them reading the clock.
        Assert.Equal(2, writes.Count);
        Assert.All(writes, write => Assert.DoesNotContain("now()", write, StringComparison.Ordinal));

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.Equal(13, recorded.HighestObservedCount);
    }

    /// <summary>The database's own clock, which is what an unstated instant is taken from.</summary>
    private static async Task<DateTimeOffset> ReadDatabaseInstantAsync(MeisterProPRDbContext db)
    {
        var instants = await db.Database
            .SqlQuery<DateTimeOffset>($"""SELECT now() AS "Value" """)
            .ToListAsync();

        return instants[0];
    }

    private async Task ResetTableAsync()
    {
        await using var db = this.CreateContext();
        await db.LicensingAuthorOverage.ExecuteDeleteAsync();
    }

    private MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }

    /// <summary>Reports the statements the context runs, so a test can assert what the two writes address.</summary>
    private MeisterProPRDbContext CreateLoggingContext(Action<string> report)
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .LogTo(report, [RelationalEventId.CommandExecuted])
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

/// <summary>
///     The branch for a provider with no upsert, which is the in-memory host the controller tests run on. It
///     reaches the same result by reading before it writes, so the rules the record depends on are asserted here
///     as well as over PostgreSQL.
/// </summary>
public sealed class AuthorOverageRepositoryInMemoryTests
{
    private static readonly DateOnly August = new(2026, 8, 1);

    private static readonly DateTimeOffset EarlyInAugust = new(2026, 8, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset LaterInAugust = new(2026, 8, 19, 17, 30, 0, TimeSpan.Zero);

    /// <summary>A few minutes after the first observation, which is inside the interval that guards the write.</summary>
    private static readonly DateTimeOffset MinutesAfterEarlyInAugust = EarlyInAugust.AddMinutes(7);

    [Fact]
    public async Task TheFirstObservationOfAMonth_InsertsBothCountsAndReportsItself()
    {
        await using var db = CreateContext();
        var sut = new AuthorOverageRepository(db);

        Assert.True(await sut.RecordAsync(August, 5, 8, EarlyInAugust));

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.Equal(new DateOnly(2026, 8, 1), recorded.OverageMonth);
        Assert.Equal(5, recorded.LicensedCount);
        Assert.Equal(8, recorded.HighestObservedCount);
    }

    [Fact]
    public async Task AHigherCountLaterInTheMonth_RaisesTheHighestCountAndMovesTheLastObservedInstant()
    {
        await using var db = CreateContext();
        var sut = new AuthorOverageRepository(db);

        await sut.RecordAsync(August, 5, 8, EarlyInAugust);
        Assert.False(await sut.RecordAsync(August, 5, 13, LaterInAugust));

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.Equal(13, recorded.HighestObservedCount);
        Assert.Equal(EarlyInAugust, recorded.FirstObservedAt);
        Assert.Equal(LaterInAugust, recorded.LastObservedAt);
    }

    [Fact]
    public async Task ALowerCountLaterInTheMonth_MovesTheLastObservedInstantOnly()
    {
        await using var db = CreateContext();
        var sut = new AuthorOverageRepository(db);

        await sut.RecordAsync(August, 5, 13, EarlyInAugust);

        // False, because the month already has a row. Only the first observation reports itself, and that is
        // what the once-per-month log line is taken from.
        Assert.False(await sut.RecordAsync(August, 5, 9, LaterInAugust));

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.Equal(13, recorded.HighestObservedCount);
        Assert.Equal(LaterInAugust, recorded.LastObservedAt);
    }

    // The branch reaches the same guard the update statement's condition applies, so a repeat observation inside
    // the interval leaves the row alone here as well.
    [Fact]
    public async Task AnUnchangedCountWithinTheInterval_LeavesTheRowExactlyAsItWas()
    {
        await using var db = CreateContext();
        var sut = new AuthorOverageRepository(db);

        await sut.RecordAsync(August, 5, 8, EarlyInAugust);
        Assert.False(await sut.RecordAsync(August, 5, 8, MinutesAfterEarlyInAugust));

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.Equal(8, recorded.HighestObservedCount);
        Assert.Equal(EarlyInAugust, recorded.LastObservedAt);
    }

    [Fact]
    public async Task AHigherCountWithinTheInterval_IsWrittenAnyway()
    {
        await using var db = CreateContext();
        var sut = new AuthorOverageRepository(db);

        await sut.RecordAsync(August, 5, 8, EarlyInAugust);
        await sut.RecordAsync(August, 5, 9, MinutesAfterEarlyInAugust);

        var recorded = await db.LicensingAuthorOverage.AsNoTracking().SingleAsync();
        Assert.Equal(9, recorded.HighestObservedCount);
        Assert.Equal(MinutesAfterEarlyInAugust, recorded.LastObservedAt);
    }

    [Fact]
    public async Task TheLicensedNumber_StaysAtWhatTheFirstObservationRecorded()
    {
        await using var db = CreateContext();
        var sut = new AuthorOverageRepository(db);

        await sut.RecordAsync(August, 5, 8, EarlyInAugust);
        await sut.RecordAsync(August, 40, 8, LaterInAugust);

        Assert.Equal(5, (await db.LicensingAuthorOverage.AsNoTracking().SingleAsync()).LicensedCount);
    }

    // The branch reaches the same rule: the row is written to the month the caller counted, not to the month
    // the instant of the write falls in.
    [Fact]
    public async Task AnObservationWrittenInTheNextMonth_IsRecordedAgainstTheMonthItCounted()
    {
        await using var db = CreateContext();
        var sut = new AuthorOverageRepository(db);

        Assert.True(await sut.RecordAsync(August, 5, 8, new DateTimeOffset(2026, 9, 1, 0, 1, 0, TimeSpan.Zero)));

        Assert.Equal(August, (await db.LicensingAuthorOverage.AsNoTracking().SingleAsync()).OverageMonth);
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"AuthorOverage-{Guid.NewGuid():N}")
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
