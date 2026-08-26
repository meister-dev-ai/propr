// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

public sealed class InstallationObservedTimeRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NothingRecorded_ReadsAsNothing()
    {
        await using var db = CreateContext();
        var sut = new InstallationObservedTimeRepository(db);

        Assert.Null(await sut.GetAsync());
    }

    [Fact]
    public async Task AFirstAdvance_RecordsTheInstant()
    {
        await using var db = CreateContext();
        var sut = new InstallationObservedTimeRepository(db);

        await sut.AdvanceToAsync(Now);

        Assert.Equal(Now, await sut.GetAsync());
        Assert.Equal(1, await db.InstallationObservedTime.CountAsync());
    }

    [Fact]
    public async Task AnAdvanceToALaterInstant_RaisesTheRecordedInstant()
    {
        await using var db = CreateContext();
        var sut = new InstallationObservedTimeRepository(db);
        await sut.AdvanceToAsync(Now);

        await sut.AdvanceToAsync(Now.AddHours(3));

        Assert.Equal(Now.AddHours(3), await sut.GetAsync());
    }

    // An installation observes one timeline, so an earlier instant is something already covered rather than a
    // correction to apply.
    [Fact]
    public async Task AnAdvanceToAnEarlierInstant_LeavesTheRecordedInstantAsItWas()
    {
        await using var db = CreateContext();
        var sut = new InstallationObservedTimeRepository(db);
        await sut.AdvanceToAsync(Now);

        await sut.AdvanceToAsync(Now.AddYears(-1));

        Assert.Equal(Now, await sut.GetAsync());
        Assert.Equal(1, await db.InstallationObservedTime.CountAsync());
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_InstallationObservedTime_{Guid.NewGuid()}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

[Collection("PostgresIntegration")]
public sealed class InstallationObservedTimeRepositoryPostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return this.ResetTablesAsync();
    }

    public Task DisposeAsync() => this.ResetTablesAsync();

    // The production write raises an existing row and cannot create one, so the first write has to reach the
    // insert the cold path takes.
    [Fact]
    public async Task AFirstAdvance_RecordsTheInstantAndSurvivesANewConnection()
    {
        await using var writing = this.CreateContext();
        await new InstallationObservedTimeRepository(writing).AdvanceToAsync(Now);

        await using var reading = this.CreateContext();

        Assert.Equal(Now, await new InstallationObservedTimeRepository(reading).GetAsync());
    }

    [Fact]
    public async Task AnAdvanceToAnEarlierInstant_LeavesTheRecordedInstantAsItWas()
    {
        await using var db = this.CreateContext();
        var sut = new InstallationObservedTimeRepository(db);
        await sut.AdvanceToAsync(Now);

        await sut.AdvanceToAsync(Now.AddYears(-1));

        Assert.Equal(Now, await sut.GetAsync());
    }

    // Replicas whose host clocks disagree write the same row at the same time, and none has one yet, so they
    // race to create it as well as to raise it. The database decides which value survives, so none of them can
    // lower it, and the highest instant any of them reported is what remains.
    [Fact]
    public async Task ConcurrentAdvances_NeverLowerTheRecordedInstant()
    {
        var instants = new[]
        {
            Now,
            Now.AddHours(-5),
            Now.AddHours(4),
            Now.AddYears(-1),
            Now.AddHours(2),
            Now.AddMinutes(-30),
        };

        var contexts = instants.Select(_ => this.CreateContext()).ToList();

        try
        {
            await Task.WhenAll(
                instants.Select((instant, index) =>
                    new InstallationObservedTimeRepository(contexts[index]).AdvanceToAsync(instant)));
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }

        await using var verification = this.CreateContext();
        var storedRow = await verification.InstallationObservedTime.AsNoTracking().SingleAsync();

        Assert.Equal(1, storedRow.Id);
        Assert.Equal(Now.AddHours(4), storedRow.ObservedAt);
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
}
