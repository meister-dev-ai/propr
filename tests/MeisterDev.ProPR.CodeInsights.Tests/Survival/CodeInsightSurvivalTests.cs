// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.CodeInsights.Survival;

namespace MeisterDev.ProPR.CodeInsights.Tests.Survival;

/// <summary>
///     Of what a review raised, how much was still being raised when the pull request finished. The interesting
///     cases are the ones that separate "the reviewer worked" from "the reviewer stopped saying it".
/// </summary>
public sealed class CodeInsightSurvivalTests : IDisposable
{
    private static readonly Guid ClientA = Guid.Parse("cccccccc-1111-1111-1111-111111111111");
    private static readonly Guid ClientB = Guid.Parse("dddddddd-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset FirstReview = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    private const string Retry =
        "The retry loop has no ceiling: a persistent 409 from the payment gateway will retry indefinitely.";

    private const string Currency =
        "The currency code is compared case-sensitively, so 'eur' never matches 'EUR'.";

    private const string Ledger =
        "The ledger write and the balance update are not performed inside one transaction.";

    private readonly MeisterProPRDbContext _dbContext;
    private readonly CodeInsightFindingStore _store;
    private readonly CodeInsightSurvivalReader _reader;

    public CodeInsightSurvivalTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"CodeInsightSurvivalTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._store = new CodeInsightFindingStore(this._dbContext, CreateCodec());
        this._reader = new CodeInsightSurvivalReader(this._dbContext);
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task AProblemRestatedInTheNextIncrementCountsAsPersisting()
    {
        var key = NewKey(ClientA, 7);
        await this.MaterialiseAsync(key, "rev-1", FirstReview, [("a.cs", Retry), ("a.cs", Currency)]);
        // Only the retry finding comes back, in slightly different words.
        await this.MaterialiseAsync(
            key,
            "rev-2",
            FirstReview.AddDays(1),
            [("a.cs", "The retry loop still has no ceiling: a persistent 409 from the payment gateway retries indefinitely.")]);

        var survival = await this._reader.GetSurvivalAsync(this.Window(ClientA));

        Assert.Equal(1, survival.Persisted);
        Assert.Equal(1, survival.Dropped);
        Assert.Equal(0, survival.Fixed);
        Assert.Equal(2, survival.Total);
        Assert.Equal(0.5, survival.PersistenceRate);
        Assert.Equal(1, survival.PullRequests);
    }

    [Fact]
    public async Task AProblemThatStoppedBeingRaisedAndHasACorroboratedFixIsCountedApart()
    {
        // The distinction that matters: the reviewer working, versus the reviewer going quiet.
        var key = NewKey(ClientA, 7);
        await this.MaterialiseAsync(key, "rev-1", FirstReview, [("a.cs", Retry), ("a.cs", Currency)]);

        var currency = (await this._store.GetFindingsForPullRequestAsync(key))
            .Single(finding => finding.Message == Currency);
        await this._store.RecordDispositionAsync(currency.Id, Disposition(CodeInsightDisposition.Addressed));

        await this.MaterialiseAsync(key, "rev-2", FirstReview.AddDays(1), [("a.cs", Retry)]);

        var survival = await this._reader.GetSurvivalAsync(this.Window(ClientA));

        Assert.Equal(1, survival.Persisted);
        Assert.Equal(1, survival.Fixed);
        Assert.Equal(0, survival.Dropped);
    }

    [Fact]
    public async Task APullRequestReviewedOnceSaysNothingAboutDurability()
    {
        // Every chain is trivially at the newest revision there, so counting it would report perfect persistence
        // for work that never had the chance to shed anything.
        var key = NewKey(ClientA, 7);
        await this.MaterialiseAsync(key, "rev-1", FirstReview, [("a.cs", Retry), ("a.cs", Currency)]);

        var survival = await this._reader.GetSurvivalAsync(this.Window(ClientA));

        Assert.Equal(0, survival.Total);
        Assert.Equal(0, survival.PullRequests);
        // Undefined rather than zero: nothing was measured, which is not the same as nothing persisting.
        Assert.Null(survival.PersistenceRate);
    }

    [Fact]
    public async Task AProblemRaisedInEveryIncrementPersists()
    {
        var key = NewKey(ClientA, 7);
        await this.MaterialiseAsync(key, "rev-1", FirstReview, [("a.cs", Retry)]);
        await this.MaterialiseAsync(key, "rev-2", FirstReview.AddDays(1), [("a.cs", Retry)]);
        await this.MaterialiseAsync(key, "rev-3", FirstReview.AddDays(2), [("a.cs", Retry)]);

        var survival = await this._reader.GetSurvivalAsync(this.Window(ClientA));

        Assert.Equal(1, survival.Persisted);
        Assert.Equal(0, survival.Dropped);
        Assert.Equal(1d, survival.PersistenceRate);
    }

    [Fact]
    public async Task AProblemThatDisappearedAndCameBackIsANewChain()
    {
        // The honest reading: the reviewer stopped reporting it and started again. Treating that as one unbroken
        // chain would hide exactly the inconsistency this measurement exists to show.
        var key = NewKey(ClientA, 7);
        await this.MaterialiseAsync(key, "rev-1", FirstReview, [("a.cs", Retry)]);
        await this.MaterialiseAsync(key, "rev-2", FirstReview.AddDays(1), [("a.cs", Currency)]);
        await this.MaterialiseAsync(key, "rev-3", FirstReview.AddDays(2), [("a.cs", Retry)]);

        var survival = await this._reader.GetSurvivalAsync(this.Window(ClientA));

        // rev-1's retry and rev-2's currency both stopped; rev-3's retry is a fresh chain that persists.
        Assert.Equal(1, survival.Persisted);
        Assert.Equal(2, survival.Dropped);
    }

    [Fact]
    public async Task TheNewestIncrementIsDerivedFromTheDataNotFromArrivalOrder()
    {
        // Increments can be re-processed out of order; letting the last write claim to be newest would make every
        // persisting chain look abandoned.
        var key = NewKey(ClientA, 7);
        await this.MaterialiseAsync(key, "rev-2", FirstReview.AddDays(1), [("a.cs", Retry)]);
        await this.MaterialiseAsync(key, "rev-1", FirstReview, [("a.cs", Retry), ("a.cs", Currency)]);

        var survival = await this._reader.GetSurvivalAsync(this.Window(ClientA));

        var aggregate = await this._dbContext.CodeInsightPullRequests.SingleAsync();
        Assert.Equal("rev-2", aggregate.LatestRevisionKey);
        Assert.Equal(1, survival.Persisted);
    }

    [Fact]
    public async Task PerPullRequestPutsTheOnesThatShedTheMostFirst()
    {
        var leaky = NewKey(ClientA, 1);
        await this.MaterialiseAsync(leaky, "rev-1", FirstReview, [("a.cs", Retry), ("a.cs", Currency), ("a.cs", Ledger)]);
        await this.MaterialiseAsync(leaky, "rev-2", FirstReview.AddDays(1), [("a.cs", Retry)]);

        var steady = NewKey(ClientA, 2);
        await this.MaterialiseAsync(steady, "rev-1", FirstReview, [("b.cs", Retry)]);
        await this.MaterialiseAsync(steady, "rev-2", FirstReview.AddDays(1), [("b.cs", Retry)]);

        var rows = await this._reader.GetSurvivalByPullRequestAsync(this.Window(ClientA), 10);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0].PullRequestId);
        Assert.Equal(2, rows[0].Counts.Dropped);
        Assert.Equal(2, rows[0].Revisions);
        Assert.Equal(0, rows[1].Counts.Dropped);
    }

    [Fact]
    public async Task AnotherClientsPullRequestsAreNeverCounted()
    {
        var mine = NewKey(ClientA, 1);
        await this.MaterialiseAsync(mine, "rev-1", FirstReview, [("a.cs", Retry)]);
        await this.MaterialiseAsync(mine, "rev-2", FirstReview.AddDays(1), [("a.cs", Retry)]);

        var theirs = NewKey(ClientB, 1);
        await this.MaterialiseAsync(theirs, "rev-1", FirstReview, [("a.cs", Retry), ("a.cs", Currency)]);
        await this.MaterialiseAsync(theirs, "rev-2", FirstReview.AddDays(1), [("a.cs", Currency)]);

        var survival = await this._reader.GetSurvivalAsync(this.Window(ClientA));

        Assert.Equal(1, survival.PullRequests);
        Assert.Equal(1, survival.Persisted);
        Assert.Equal(0, survival.Dropped);
    }

    [Fact]
    public async Task AnEmptyAuthorisedClientSetMeasuresNothing()
    {
        var key = NewKey(ClientA, 7);
        await this.MaterialiseAsync(key, "rev-1", FirstReview, [("a.cs", Retry)]);
        await this.MaterialiseAsync(key, "rev-2", FirstReview.AddDays(1), [("a.cs", Retry)]);

        Assert.Equal(0, (await this._reader.GetSurvivalAsync(this.Window())).Total);
        Assert.Empty(await this._reader.GetSurvivalByPullRequestAsync(this.Window(), 10));
    }

    [Fact]
    public async Task ReMaterialisingAnIncrementDoesNotDuplicateChains()
    {
        // The crawl re-delivers the same increment; the natural key makes the rows idempotent and the chains must
        // be too, or persistence would inflate on every redelivery.
        var key = NewKey(ClientA, 7);
        await this.MaterialiseAsync(key, "rev-1", FirstReview, [("a.cs", Retry), ("a.cs", Currency)]);
        await this.MaterialiseAsync(key, "rev-2", FirstReview.AddDays(1), [("a.cs", Retry)]);
        await this.MaterialiseAsync(key, "rev-2", FirstReview.AddDays(1), [("a.cs", Retry)]);

        var survival = await this._reader.GetSurvivalAsync(this.Window(ClientA));

        Assert.Equal(1, survival.Persisted);
        Assert.Equal(1, survival.Dropped);
        Assert.Equal(2, survival.Total);
    }

    private CodeInsightRollupQuery Window(params Guid[] clientIds)
    {
        return new CodeInsightRollupQuery(clientIds, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
    }

    private static CodeInsightPullRequestKey NewKey(Guid clientId, long pullRequestId)
    {
        return new CodeInsightPullRequestKey(clientId, "repo-1", pullRequestId);
    }

    private Task MaterialiseAsync(
        CodeInsightPullRequestKey key,
        string revisionKey,
        DateTimeOffset observedAt,
        (string? FilePath, string Message)[] findings)
    {
        var snapshots = findings
            .Select((finding, ordinal) => new CodeInsightFindingSnapshot(
                ordinal,
                finding.FilePath,
                10 + ordinal,
                CommentSeverity.Error,
                finding.Message,
                "Baseline",
                null,
                null,
                false,
                ReviewCommentScopeRelation.OnChangedLine,
                null,
                $"thread-{revisionKey}-{ordinal}",
                $"comment-{revisionKey}-{ordinal}"))
            .ToList();

        return this._store.MaterialiseFindingsAsync(key, Guid.NewGuid(), revisionKey, observedAt, snapshots);
    }

    private static CodeInsightDispositionRecord Disposition(CodeInsightDisposition disposition)
    {
        return new CodeInsightDispositionRecord(
            disposition,
            ThreadResolutionIntent.ClaimsFix,
            ThreadAnchorCodeChange.Changed,
            null,
            null);
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.CodeInsightSurvivalTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }
}
