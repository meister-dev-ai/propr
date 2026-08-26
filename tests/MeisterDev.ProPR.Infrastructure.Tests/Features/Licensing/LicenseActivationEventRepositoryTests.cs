// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

public sealed class LicenseActivationEventRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NoChangesRecorded_ReadsAsAnEmptyHistory()
    {
        await using var db = CreateContext();

        Assert.Empty(await new LicenseActivationEventRepository(db).ListRecentAsync(10));
    }

    [Fact]
    public async Task ARecordedChange_IsReadBackWithEveryFieldItCarried()
    {
        var actor = Guid.NewGuid();
        await using var db = CreateContext();
        var sut = new LicenseActivationEventRepository(db);

        await sut.RecordAsync(
            new LicenseActivationEvent
            {
                Action = LicenseActivationAction.Replaced,
                OccurredAt = Now,
                ActorUserId = actor,
                LicenseId = "0f4c1b7a-6d21-4f36-9e18-5a7b3c9d2e40",
                Licensee = "Northwind Traders",
            });

        var recorded = Assert.Single(await sut.ListRecentAsync(10));
        Assert.Equal(LicenseActivationAction.Replaced, recorded.Action);
        Assert.Equal(Now, recorded.OccurredAt);
        Assert.Equal(actor, recorded.ActorUserId);
        Assert.Equal("0f4c1b7a-6d21-4f36-9e18-5a7b3c9d2e40", recorded.LicenseId);
        Assert.Equal("Northwind Traders", recorded.Licensee);
    }

    // A removal of a document this build establishes no identity for is still recorded, so the history shows
    // that the installation's license changed even when it cannot name which license it was.
    [Fact]
    public async Task AChangeWithoutAnEstablishedIdentity_IsRecorded()
    {
        await using var db = CreateContext();
        var sut = new LicenseActivationEventRepository(db);

        await sut.RecordAsync(new LicenseActivationEvent { Action = LicenseActivationAction.Removed, OccurredAt = Now });

        var recorded = Assert.Single(await sut.ListRecentAsync(10));
        Assert.Equal(LicenseActivationAction.Removed, recorded.Action);
        Assert.Null(recorded.LicenseId);
        Assert.Null(recorded.Licensee);
        Assert.Null(recorded.ActorUserId);
    }

    [Fact]
    public async Task TheHistory_IsReadNewestFirst()
    {
        await using var db = CreateContext();
        var sut = new LicenseActivationEventRepository(db);

        await sut.RecordAsync(Event(LicenseActivationAction.Activated, Now, "first-license"));
        await sut.RecordAsync(Event(LicenseActivationAction.Replaced, Now.AddHours(2), "second-license"));
        await sut.RecordAsync(Event(LicenseActivationAction.Removed, Now.AddHours(4), "second-license"));

        var history = await sut.ListRecentAsync(10);

        Assert.Equal(
            [LicenseActivationAction.Removed, LicenseActivationAction.Replaced, LicenseActivationAction.Activated],
            history.Select(entry => entry.Action));
    }

    [Fact]
    public async Task TheHistory_IsBoundedByTheRequestedCount()
    {
        await using var db = CreateContext();
        var sut = new LicenseActivationEventRepository(db);

        for (var index = 0; index < 5; index++)
        {
            await sut.RecordAsync(Event(LicenseActivationAction.Replaced, Now.AddHours(index), $"license-{index}"));
        }

        var history = await sut.ListRecentAsync(2);

        Assert.Equal(["license-4", "license-3"], history.Select(entry => entry.LicenseId));
    }

    internal static LicenseActivationEvent Event(
        LicenseActivationAction action,
        DateTimeOffset occurredAt,
        string licenseId)
    {
        return new LicenseActivationEvent
        {
            Action = action,
            OccurredAt = occurredAt,
            LicenseId = licenseId,
            Licensee = "Northwind Traders",
        };
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_LicenseActivationEvents_{Guid.NewGuid()}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

[Collection("PostgresIntegration")]
public sealed class LicenseActivationEventRepositoryPostgresTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return this.ResetTablesAsync();
    }

    public Task DisposeAsync()
    {
        return this.ResetTablesAsync();
    }

    // The ordering and the timestamp round trip run in the database, which the in-memory provider does not
    // exercise, and a record has to keep the instant it was written with.
    [Fact]
    public async Task TheHistory_IsReadNewestFirstAcrossConnections()
    {
        await using var db = this.CreatePostgresContext();
        var sut = new LicenseActivationEventRepository(db);

        await sut.RecordAsync(LicenseActivationEventRepositoryTests.Event(LicenseActivationAction.Activated, Now, "first-license"));
        await sut.RecordAsync(LicenseActivationEventRepositoryTests.Event(LicenseActivationAction.Replaced, Now.AddHours(2), "second-license"));

        await using var reading = this.CreatePostgresContext();
        var history = await new LicenseActivationEventRepository(reading).ListRecentAsync(10);

        Assert.Equal(["second-license", "first-license"], history.Select(entry => entry.LicenseId));
        Assert.Equal(Now.AddHours(2), history[0].OccurredAt);
        Assert.Equal(LicenseActivationAction.Replaced, history[0].Action);
    }

    // The record outlives the license it describes: the license row is a separate table, so removing the
    // license leaves the history intact.
    [Fact]
    public async Task TheHistory_SurvivesRemovalOfTheLicenseItDescribes()
    {
        using var chain = LicenseTestChain.Create();
        await using var db = this.CreatePostgresContext();
        var licenseStore = new InstallationLicenseRepository(
            db,
            InstallationLicenseRepositoryTests.CreateCodec(),
            TimeProvider.System);
        var sut = new LicenseActivationEventRepository(db);

        await licenseStore.SetAsync(chain.Sign(LicenseTestChain.ClaimsFor(Now.AddDays(-1), Now.AddYears(1))), null);
        await sut.RecordAsync(LicenseActivationEventRepositoryTests.Event(LicenseActivationAction.Activated, Now, "first-license"));
        await licenseStore.RemoveAsync();
        await sut.RecordAsync(LicenseActivationEventRepositoryTests.Event(LicenseActivationAction.Removed, Now.AddHours(1), "first-license"));

        Assert.Null(await licenseStore.GetAsync());
        Assert.Equal(2, (await sut.ListRecentAsync(10)).Count);
    }

    private async Task ResetTablesAsync()
    {
        await using var db = this.CreatePostgresContext();
        await db.LicenseActivationEvents.ExecuteDeleteAsync();
        await db.InstallationLicenses.ExecuteDeleteAsync();
    }

    private MeisterProPRDbContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
