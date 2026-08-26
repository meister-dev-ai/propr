// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

/// <summary>
///     The month-and-author rollup. The upsert is what keeps a month counting people rather than completions,
///     and the month itself comes from the database clock, so both run against PostgreSQL.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class AuthorActivityRollupRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly ProviderHostRef AzureHost =
        new(ScmProvider.AzureDevOps, "https://dev.azure.com/acme");

    private static readonly ProviderHostRef GitHubHost = new(ScmProvider.GitHub, "https://github.com");

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        return this.ResetTableAsync();
    }

    public Task DisposeAsync()
    {
        return this.ResetTableAsync();
    }

    [Fact]
    public async Task AnAuthorRecordedTwiceInAMonth_LeavesOneRowAndOneCount()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);

        await sut.RecordAuthorAsync(AzureHost, "vss-guid-1", AuthorActivitySource.Review, excluded: false);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-1", AuthorActivitySource.Review, excluded: false);

        Assert.Single(await db.LicensingAuthorActivity.AsNoTracking().ToListAsync());
        Assert.Equal(1, await sut.CountCurrentMonthAuthorsAsync());
    }

    // The whole point of the native identifier: on Azure DevOps a reviewed pull request and an answered mention
    // both name the account by its VSS identity GUID, so the same person is one author across the two.
    [Fact]
    public async Task OneAzureDevOpsAccountReviewedAndAnswered_CountsOnce()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);

        await sut.RecordAuthorAsync(
            AzureHost,
            "6f0c1a2b-3d4e-5f60-7182-93a4b5c6d7e8",
            AuthorActivitySource.Review,
            excluded: false);
        await sut.RecordAuthorAsync(
            AzureHost,
            "6f0c1a2b-3d4e-5f60-7182-93a4b5c6d7e8",
            AuthorActivitySource.MentionAnswer,
            excluded: false);

        Assert.Equal(1, await sut.CountCurrentMonthAuthorsAsync());

        // The first observation is the one the row keeps, so the source names where the author was first seen.
        var recorded = await db.LicensingAuthorActivity.AsNoTracking().SingleAsync();
        Assert.Equal(AuthorActivitySource.Review, recorded.FirstSeenSource);
    }

    [Fact]
    public async Task OneGitHubAccountReviewedAndAnswered_CountsOnce()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);

        await sut.RecordAuthorAsync(GitHubHost, "4242", AuthorActivitySource.Review, excluded: false);
        await sut.RecordAuthorAsync(GitHubHost, "4242", AuthorActivitySource.MentionAnswer, excluded: false);

        Assert.Equal(1, await sut.CountCurrentMonthAuthorsAsync());
    }

    // A provider-native identifier is unique only within one host, so two hosts issuing the same digits are
    // two people and must not collapse into one author. Only the host differs here, so nothing but the host
    // can be what keeps them apart.
    [Fact]
    public async Task TheSameIdentifierOnTwoHostsOfOneProvider_CountsTwice()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);

        await sut.RecordAuthorAsync(
            new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com"),
            "4242",
            AuthorActivitySource.Review, excluded: false);
        await sut.RecordAuthorAsync(
            new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.other.example"),
            "4242",
            AuthorActivitySource.Review, excluded: false);

        Assert.Equal(2, await sut.CountCurrentMonthAuthorsAsync());
    }

    [Fact]
    public async Task AnExcludedAuthor_IsNotCounted()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-2", AuthorActivitySource.Review, excluded: false);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-3", AuthorActivitySource.Review, excluded: false);

        await db.LicensingAuthorActivity
            .Where(row => row.ExternalUserId == "vss-guid-2")
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Excluded, true));

        Assert.Equal(1, await sut.CountCurrentMonthAuthorsAsync());
    }

    // The exclusion rises on any observation in the month. The account was counted when it was first seen and
    // identified as automation later, and the month has to follow the later signal.
    [Fact]
    public async Task AnAuthorCountedThenSeenAsAutomation_BecomesExcludedForTheMonth()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);

        await sut.RecordAuthorAsync(AzureHost, "vss-guid-6", AuthorActivitySource.Review, excluded: false);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-6", AuthorActivitySource.MentionAnswer, excluded: true);

        var recorded = await db.LicensingAuthorActivity.AsNoTracking().SingleAsync();
        Assert.True(recorded.Excluded);
        Assert.Equal(0, await sut.CountCurrentMonthAuthorsAsync());

        // The escalation updates the flag alone, so where the author was first seen is still what the row says.
        Assert.Equal(AuthorActivitySource.Review, recorded.FirstSeenSource);
    }

    // The reverse never happens. An account stated to be automation once stays out of the month, because
    // asking a question afterwards does not withdraw that statement.
    [Fact]
    public async Task AnExcludedAuthorSeenAgainWithoutTheSignal_StaysExcluded()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);

        await sut.RecordAuthorAsync(AzureHost, "vss-guid-7", AuthorActivitySource.Review, excluded: true);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-7", AuthorActivitySource.Review, excluded: false);

        var recorded = await db.LicensingAuthorActivity.AsNoTracking().SingleAsync();
        Assert.True(recorded.Excluded);
        Assert.Equal(0, await sut.CountCurrentMonthAuthorsAsync());
    }

    [Fact]
    public async Task TheExcludedCount_CountsTheCurrentMonthsExcludedAuthorsOnly()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);
        var currentMonth = await ReadCurrentMonthAsync(db);

        await SeedAuthorsAsync(db, currentMonth.AddMonths(-1), 4, excluded: true);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-8", AuthorActivitySource.Review, excluded: true);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-9", AuthorActivitySource.Review, excluded: true);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-10", AuthorActivitySource.Review, excluded: false);

        Assert.Equal(2, await sut.CountCurrentMonthExcludedAuthorsAsync());
        Assert.Equal(1, await sut.CountCurrentMonthAuthorsAsync());
    }

    [Fact]
    public async Task TheCurrentMonthCounts_UseOneMonthForCountedAndExcludedAuthors()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);
        var currentMonth = await ReadCurrentMonthAsync(db);

        await SeedAuthorsAsync(db, currentMonth.AddMonths(-1), 4, excluded: true);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-counted", AuthorActivitySource.Review, excluded: false);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-excluded", AuthorActivitySource.Review, excluded: true);

        var counts = await sut.GetCurrentMonthCountsAsync();

        Assert.Equal(currentMonth, counts.Month);
        Assert.Equal(1, counts.Counted);
        Assert.Equal(1, counts.Excluded);
    }

    // The month a completion falls in turns over at midnight UTC. Two completions a couple of minutes apart
    // across that boundary belong to different months, and an author is counted once in each of them.
    [Fact]
    public async Task CompletionsEitherSideOfMidnightUtc_LandInDifferentMonths()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);

        var lastMinuteOfJanuary = new DateTimeOffset(2026, 1, 31, 23, 59, 0, TimeSpan.Zero);
        var firstMinuteOfFebruary = new DateTimeOffset(2026, 2, 1, 0, 1, 0, TimeSpan.Zero);

        await sut.RecordAuthorAsync(
            AzureHost,
            "vss-guid-4",
            AuthorActivitySource.Review,
            excluded: false,
            lastMinuteOfJanuary);
        await sut.RecordAuthorAsync(
            AzureHost,
            "vss-guid-4",
            AuthorActivitySource.Review,
            excluded: false,
            firstMinuteOfFebruary);

        var months = await db.LicensingAuthorActivity
            .AsNoTracking()
            .OrderBy(row => row.ActivityMonth)
            .Select(row => row.ActivityMonth)
            .ToListAsync();

        Assert.Equal([new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1)], months);
    }

    // The instant is read as UTC whatever offset it carries, so a completion stated in a zone ahead of UTC does
    // not slide into the next month.
    [Fact]
    public async Task ACompletionStatedInAnotherZone_LandsInTheMonthItsUtcInstantFallsIn()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);

        // 2026-02-01T10:30 at +14:00 is 2026-01-31T20:30 UTC, which is still January.
        var aheadOfUtc = new DateTimeOffset(2026, 2, 1, 10, 30, 0, TimeSpan.FromHours(14));

        await sut.RecordAuthorAsync(
            AzureHost,
            "vss-guid-5",
            AuthorActivitySource.Review,
            excluded: false,
            aheadOfUtc);

        var recorded = await db.LicensingAuthorActivity.AsNoTracking().SingleAsync();
        Assert.Equal(new DateOnly(2026, 1, 1), recorded.ActivityMonth);
    }

    [Fact]
    public async Task TheTrailingYearPeak_IsTheBusiestMonthInTheWindowAndItsCount()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);
        var currentMonth = await ReadCurrentMonthAsync(db);

        await SeedAuthorsAsync(db, currentMonth.AddMonths(-4), 2);
        await SeedAuthorsAsync(db, currentMonth.AddMonths(-2), 5);
        await SeedAuthorsAsync(db, currentMonth, 3);

        var peak = await sut.GetTrailingYearPeakAsync();

        Assert.NotNull(peak);
        Assert.Equal(currentMonth.AddMonths(-2), peak!.Month);
        Assert.Equal(5, peak.AuthorCount);
    }

    // Twelve months including the current one. A busier month outside that window is history the licensed
    // period no longer covers, so it is not the peak.
    [Fact]
    public async Task TheTrailingYearPeak_IgnoresMonthsOutsideTheWindow()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);
        var currentMonth = await ReadCurrentMonthAsync(db);

        await SeedAuthorsAsync(db, currentMonth.AddMonths(-12), 9);
        await SeedAuthorsAsync(db, currentMonth.AddMonths(-11), 4);

        var peak = await sut.GetTrailingYearPeakAsync();

        Assert.Equal(currentMonth.AddMonths(-11), peak!.Month);
        Assert.Equal(4, peak.AuthorCount);
    }

    [Fact]
    public async Task TheTrailingYearPeak_ExcludedAuthorsAreNotCountedTowardsIt()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);
        var currentMonth = await ReadCurrentMonthAsync(db);

        await SeedAuthorsAsync(db, currentMonth.AddMonths(-3), 6, excluded: true);
        await SeedAuthorsAsync(db, currentMonth.AddMonths(-1), 2);

        var peak = await sut.GetTrailingYearPeakAsync();

        Assert.Equal(currentMonth.AddMonths(-1), peak!.Month);
        Assert.Equal(2, peak.AuthorCount);
    }

    // The peak read filters on the same flag the write sets, so an author excluded at record time is out of
    // both reads and not only out of the current month's count.
    [Fact]
    public async Task TheTrailingYearPeak_LeavesOutAnAuthorRecordedAsAutomation()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);
        var currentMonth = await ReadCurrentMonthAsync(db);

        await sut.RecordAuthorAsync(AzureHost, "vss-guid-11", AuthorActivitySource.Review, excluded: true);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-12", AuthorActivitySource.Review, excluded: false);

        var peak = await sut.GetTrailingYearPeakAsync();

        Assert.Equal(currentMonth, peak!.Month);
        Assert.Equal(1, peak.AuthorCount);
    }

    [Fact]
    public async Task TheTrailingYearPeak_WithNoCountedAuthorInTheWindow_IsAbsent()
    {
        await using var db = this.CreateContext();

        Assert.Null(await new AuthorActivityRollupRepository(db).GetTrailingYearPeakAsync());
    }

    [Fact]
    public async Task TheCurrentMonthCount_LeavesOutEarlierMonths()
    {
        await using var db = this.CreateContext();
        var sut = new AuthorActivityRollupRepository(db);
        var currentMonth = await ReadCurrentMonthAsync(db);

        await SeedAuthorsAsync(db, currentMonth.AddMonths(-1), 4);
        await SeedAuthorsAsync(db, currentMonth, 2);

        Assert.Equal(2, await sut.CountCurrentMonthAuthorsAsync());
    }

    private static async Task SeedAuthorsAsync(
        MeisterProPRDbContext db,
        DateOnly month,
        int count,
        bool excluded = false)
    {
        for (var index = 0; index < count; index++)
        {
            var externalUserId = $"seeded-{month:yyyy-MM}-{index}";
            db.LicensingAuthorActivity.Add(
                new LicensingAuthorActivityRecord
                {
                    ActivityMonth = month,
                    AuthorKey = AzureHost.ScopedKey(externalUserId),
                    Excluded = excluded,
                    Provider = AzureHost.Provider,
                    HostBaseUrl = AzureHost.HostBaseUrl,
                    ExternalUserId = externalUserId,
                    FirstSeenAt = DateTimeOffset.UtcNow,
                    FirstSeenSource = AuthorActivitySource.Review,
                });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>The month the database's own clock is in, which is the month the reads under test window on.</summary>
    private static async Task<DateOnly> ReadCurrentMonthAsync(MeisterProPRDbContext db)
    {
        var months = await db.Database
            .SqlQuery<DateOnly>($"""SELECT date_trunc('month', now() AT TIME ZONE 'UTC')::date AS "Value" """)
            .ToListAsync();

        return months[0];
    }

    private async Task ResetTableAsync()
    {
        await using var db = this.CreateContext();
        await db.LicensingAuthorActivity.ExecuteDeleteAsync();
    }

    private MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
            .Options;

        return new MeisterProPRDbContext(options);
    }
}

/// <summary>
///     The branch for a provider with no upsert, which is the in-memory host the controller tests run on. It
///     reaches the same result by reading before it writes, so the rules the count depends on are asserted here
///     as well as over PostgreSQL.
/// </summary>
public sealed class AuthorActivityRollupRepositoryInMemoryTests
{
    private static readonly ProviderHostRef AzureHost =
        new(ScmProvider.AzureDevOps, "https://dev.azure.com/acme");

    [Fact]
    public async Task AnAuthorRecordedTwiceInAMonth_LeavesOneRowAndOneCount()
    {
        await using var db = CreateContext();
        var sut = new AuthorActivityRollupRepository(db);

        await sut.RecordAuthorAsync(AzureHost, "vss-guid-1", AuthorActivitySource.Review, excluded: false);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-1", AuthorActivitySource.MentionAnswer, excluded: false);

        Assert.Single(await db.LicensingAuthorActivity.AsNoTracking().ToListAsync());
        Assert.Equal(1, await sut.CountCurrentMonthAuthorsAsync());
    }

    [Fact]
    public async Task AnExcludedAuthor_IsNotCounted()
    {
        await using var db = CreateContext();
        var sut = new AuthorActivityRollupRepository(db);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-2", AuthorActivitySource.Review, excluded: false);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-3", AuthorActivitySource.Review, excluded: false);

        var excluded = await db.LicensingAuthorActivity
            .SingleAsync(row => row.ExternalUserId == "vss-guid-2");
        excluded.Excluded = true;
        await db.SaveChangesAsync();

        Assert.Equal(1, await sut.CountCurrentMonthAuthorsAsync());
    }

    [Fact]
    public async Task AnAuthorCountedThenSeenAsAutomation_BecomesExcludedForTheMonth()
    {
        await using var db = CreateContext();
        var sut = new AuthorActivityRollupRepository(db);

        await sut.RecordAuthorAsync(AzureHost, "vss-guid-4", AuthorActivitySource.Review, excluded: false);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-4", AuthorActivitySource.MentionAnswer, excluded: true);

        Assert.True((await db.LicensingAuthorActivity.AsNoTracking().SingleAsync()).Excluded);
        Assert.Equal(0, await sut.CountCurrentMonthAuthorsAsync());
        Assert.Equal(1, await sut.CountCurrentMonthExcludedAuthorsAsync());
    }

    [Fact]
    public async Task AnExcludedAuthorSeenAgainWithoutTheSignal_StaysExcluded()
    {
        await using var db = CreateContext();
        var sut = new AuthorActivityRollupRepository(db);

        await sut.RecordAuthorAsync(AzureHost, "vss-guid-5", AuthorActivitySource.Review, excluded: true);
        await sut.RecordAuthorAsync(AzureHost, "vss-guid-5", AuthorActivitySource.Review, excluded: false);

        Assert.True((await db.LicensingAuthorActivity.AsNoTracking().SingleAsync()).Excluded);
        Assert.Equal(1, await sut.CountCurrentMonthExcludedAuthorsAsync());
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"AuthorActivityRollup-{Guid.NewGuid():N}")
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
