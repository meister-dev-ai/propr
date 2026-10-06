// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Persistence;

namespace MeisterDev.ProPR.CodeInsights.Tests.Metrics;

/// <summary>
///     The drill-through read. A metric nobody can open up is a number nobody can check, so this path exists,
///     and because it returns the findings themselves, its client filter matters as much as any aggregate's.
/// </summary>
public sealed class CodeInsightBrowseReaderTests : IDisposable
{
    private static readonly Guid ClientA = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ClientB = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTimeOffset ReviewedAt = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A stored discussion that is ProPR speaking: a summary ending in the AI-generated marker.</summary>
    private static readonly string OwnMarkedComment =
        "0caeb875-08d2-6d69: ## Meister Review Bot Review\nThis PR adds...\n"
        + TestPostedCommentComposer.Default.Text;

    private readonly MeisterProPRDbContext _dbContext;
    private readonly ISecretProtectionCodec _codec;
    private readonly CodeInsightFindingStore _store;
    private readonly CodeInsightBrowseReader _reader;

    public CodeInsightBrowseReaderTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"CodeInsightBrowseReaderTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._codec = CreateCodec();
        this._store = new CodeInsightFindingStore(this._dbContext, this._codec);
        this._reader = new CodeInsightBrowseReader(this._dbContext, this._codec, TestPostedCommentComposer.Default);
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task AFindingComesBackWithItsScopeItsTextAndItsOutcome()
    {
        var key = await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs", "b.cs"]);
        var findings = await this._store.GetFindingsForPullRequestAsync(key);
        await this._store.ApplyClassificationAsync(findings[0].Id, Classification("logic-error"));
        await this._store.RecordDispositionAsync(findings[0].Id, Disposition(CodeInsightDisposition.Addressed));

        var rows = await this._reader.ListFindingsAsync(this.Query(ClientA));

        Assert.Equal(2, rows.Count);
        var classified = Assert.Single(rows, row => row.CoreTags.Count > 0);
        Assert.Equal(ClientA, classified.ClientId);
        Assert.Equal("repo-1", classified.RepositoryId);
        Assert.Equal(7, classified.PullRequestId);
        Assert.Equal("logic-error", Assert.Single(classified.CoreTags));
        Assert.Equal(CodeInsightDisposition.Addressed, classified.Disposition);
        // The text is decrypted for the caller; it is stored encrypted.
        Assert.Contains("Finding", classified.Message, StringComparison.Ordinal);
        Assert.NotNull(classified.ProviderThreadId);
    }

    [Fact]
    public async Task FailedHumanJudgementIsExposedAsUnavailableByTheBrowseResponse()
    {
        var key = await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs"]);
        await this._store.RecordMissAsync(key, Miss("failed-human", false) with { JudgementFailed = true, Confidence = null });
        var row = Assert.Single(await this._reader.ListMissesAsync(this.Query(ClientA)));
        var json = System.Text.Json.JsonSerializer.SerializeToElement(
            row, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.True(json.TryGetProperty("judgementFailed", out var failed));
        Assert.True(failed.GetBoolean());
        Assert.False(row.CountsAsMiss);
    }

    [Fact]
    public async Task AFindingWhoseThreadHasNotResolvedHasNoOutcomeRatherThanADefaultOne()
    {
        await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs"]);

        var row = Assert.Single(await this._reader.ListFindingsAsync(this.Query(ClientA)));

        Assert.Null(row.Disposition);
        Assert.Empty(row.CoreTags);
    }

    [Fact]
    public async Task AnotherClientsFindingsAreNeverReturned()
    {
        await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs"]);
        await this.SeedAsync(ClientB, "repo-1", 7, ["a.cs", "b.cs"]);

        var rows = await this._reader.ListFindingsAsync(this.Query(ClientA));

        Assert.All(rows, row => Assert.Equal(ClientA, row.ClientId));
        Assert.Single(rows);
    }

    [Fact]
    public async Task AnEmptyAuthorisedClientSetReturnsNothingRatherThanEverything()
    {
        await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs"]);

        Assert.Empty(await this._reader.ListFindingsAsync(this.Query()));
        Assert.Empty(await this._reader.ListMissesAsync(this.Query()));
    }

    [Fact]
    public async Task NarrowingByRepositoryPullRequestAndFileEachTakesEffect()
    {
        await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs", "b.cs"]);
        await this.SeedAsync(ClientA, "repo-2", 8, ["c.cs"]);

        var byRepository = await this._reader.ListFindingsAsync(this.Query(ClientA) with { RepositoryId = "repo-2" });
        var byPullRequest = await this._reader.ListFindingsAsync(this.Query(ClientA) with { PullRequestId = 7 });
        var byFile = await this._reader.ListFindingsAsync(this.Query(ClientA) with { FilePath = "b.cs" });

        Assert.Equal("repo-2", Assert.Single(byRepository).RepositoryId);
        Assert.Equal(2, byPullRequest.Count);
        Assert.Equal("b.cs", Assert.Single(byFile).FilePath);
    }

    [Fact]
    public async Task NarrowingByCoreTypeReturnsOnlyFindingsCarryingIt()
    {
        var key = await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs", "b.cs"]);
        var findings = await this._store.GetFindingsForPullRequestAsync(key);
        await this._store.ApplyClassificationAsync(findings[0].Id, Classification("concurrency"));
        await this._store.ApplyClassificationAsync(findings[1].Id, Classification("naming-clarity"));

        var rows = await this._reader.ListFindingsAsync(this.Query(ClientA) with { CoreType = "concurrency" });

        Assert.Equal("concurrency", Assert.Single(Assert.Single(rows).CoreTags));
    }

    [Fact]
    public async Task NarrowingByOutcomeReturnsOnlyFindingsThatReachedIt()
    {
        var key = await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs", "b.cs"]);
        var findings = await this._store.GetFindingsForPullRequestAsync(key);
        await this._store.RecordDispositionAsync(findings[0].Id, Disposition(CodeInsightDisposition.FalsePositive));
        await this._store.RecordDispositionAsync(findings[1].Id, Disposition(CodeInsightDisposition.Addressed));

        var rows = await this._reader.ListFindingsAsync(this.Query(ClientA) with { Disposition = CodeInsightDisposition.FalsePositive });

        Assert.Equal(CodeInsightDisposition.FalsePositive, Assert.Single(rows).Disposition);
    }

    [Fact]
    public async Task AFindingOutsideTheWindowIsNotReturnedAndTheLastDayIsInside()
    {
        // The window is inclusive of its last day, so a review that ran late on it still belongs to it.
        await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs"], new DateTimeOffset(2026, 3, 31, 23, 30, 0, TimeSpan.Zero));
        await this.SeedAsync(ClientA, "repo-1", 8, ["b.cs"], new DateTimeOffset(2026, 4, 1, 0, 30, 0, TimeSpan.Zero));

        var rows = await this._reader.ListFindingsAsync(new CodeInsightBrowseQuery([ClientA], new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));

        Assert.Equal("a.cs", Assert.Single(rows).FilePath);
    }

    [Fact]
    public async Task TheNewestReviewComesFirstAndTheRowLimitIsClampedRatherThanTrusted()
    {
        await this.SeedAsync(ClientA, "repo-1", 7, ["old.cs"], ReviewedAt);
        await this.SeedAsync(ClientA, "repo-1", 8, ["new.cs"], ReviewedAt.AddDays(3));

        var ordered = await this._reader.ListFindingsAsync(this.Query(ClientA));
        var oneRow = await this._reader.ListFindingsAsync(this.Query(ClientA) with { Limit = 1 });
        var absurd = await this._reader.ListFindingsAsync(this.Query(ClientA) with { Limit = 100_000 });
        var negative = await this._reader.ListFindingsAsync(this.Query(ClientA) with { Limit = -5 });

        Assert.Equal("new.cs", ordered[0].FilePath);
        Assert.Equal("new.cs", Assert.Single(oneRow).FilePath);
        Assert.Equal(2, absurd.Count);
        // A nonsensical limit becomes one row rather than none, and never an unbounded read.
        Assert.Single(negative);
    }

    [Fact]
    public async Task FindingPagesDoNotRepeatEqualTimestampRowsAndKeepClientScope()
    {
        await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs", "b.cs", "c.cs", "d.cs"]);
        await this.SeedAsync(ClientB, "repo-1", 7, ["other.cs"]);

        var all = await this._reader.ListFindingsAsync(this.Query(ClientA));
        var first = await this._reader.ListFindingsAsync(this.Query(ClientA) with { Limit = 2 });
        var second = await this._reader.ListFindingsAsync(this.Query(ClientA) with { Limit = 2, Offset = 2 });
        var beyond = await this._reader.ListFindingsAsync(this.Query(ClientA) with { Offset = 4 });
        var negative = await this._reader.ListFindingsAsync(this.Query(ClientA) with { Limit = 2, Offset = -10 });

        Assert.Equal(4, all.Count);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.All(all, row => Assert.Equal(ClientA, row.ClientId));
        Assert.Equal(all.Select(row => row.Id), first.Concat(second).Select(row => row.Id));
        Assert.Equal(["a.cs", "b.cs", "c.cs", "d.cs"], first.Concat(second).Select(row => row.FilePath).Order());
        Assert.Equal(4, first.Concat(second).Select(row => row.Id).Distinct().Count());
        Assert.All(first, row => Assert.Equal(ClientA, row.ClientId));
        Assert.All(second, row => Assert.Equal(ClientA, row.ClientId));
        Assert.Empty(beyond);
        Assert.Equal(first.Select(row => row.Id), negative.Select(row => row.Id));
    }

    [Fact]
    public async Task HarvestedThreadsComeBackWithAllThreeJudgementsIncludingTheOnesThatDidNotQualify()
    {
        // The non-qualifying rows are the point: recall depends on where the line sits, and nobody can
        // calibrate that line without seeing what it currently excludes.
        var key = await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs"]);
        await this._store.RecordMissAsync(key, Miss("thread-1", countsAsMiss: true));
        await this._store.RecordMissAsync(key, Miss("thread-2", countsAsMiss: false));

        var rows = await this._reader.ListMissesAsync(this.Query(ClientA));

        Assert.Equal(2, rows.Count);
        var qualifying = Assert.Single(rows, row => row.CountsAsMiss);
        Assert.True(qualifying.IsSubstantive);
        Assert.True(qualifying.WasActedOn);
        Assert.True(qualifying.IsInScope);
        Assert.Equal(ClientA, qualifying.ClientId);
        Assert.Equal("repo-1", qualifying.RepositoryId);
        Assert.Contains("retry count", qualifying.Discussion, StringComparison.Ordinal);
        Assert.Single(rows, row => !row.CountsAsMiss && !row.IsInScope);
    }

    [Fact]
    public async Task RowsHarvestedUnderOlderRulesAreNotPresentedAsHumanThreads()
    {
        // Nothing re-judges a stored row, so an installation that harvested its own summary or the provider's
        // audit entries keeps those rows. A list that shows them as threads a person opened is wrong on its face.
        var key = await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs"]);
        await this._store.RecordMissAsync(key, Miss("thread-human", true));
        await this._store.RecordMissAsync(
            key,
            Miss("thread-own-summary", false, "0caeb875-08d2-6d69: **AI Review Summary**\n\nThis PR adds..."));
        await this._store.RecordMissAsync(
            key,
            Miss(
                "thread-activity",
                false,
                "00000002-0000-8888-8000-000000000000: Andreas Rain added Meister ProPR as a reviewer"));
        await this._store.RecordMissAsync(
            key,
            Miss(
                "thread-marked-summary",
                false,
                "0caeb875-08d2-6d69: ## Meister Review Bot Review\nThis PR adds...\n"
                + TestPostedCommentComposer.Default.Text));

        var rows = await this._reader.ListMissesAsync(this.Query(ClientA));

        var row = Assert.Single(rows);
        Assert.Equal("thread-human", row.ProviderThreadId);
    }

    [Fact]
    public async Task TheRowLimitCountsHumanThreadsAndNotProPrsOwnComments()
    {
        // The newest stored rows are ProPR's own. A limit applied to the query and only then to eligibility
        // would spend the caller's single row on one of them and answer with nothing.
        var key = await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs"]);
        await this._store.RecordMissAsync(key, Miss("thread-human", true));
        await this._store.RecordMissAsync(key, Miss("thread-own-older", true, OwnMarkedComment));
        await this._store.RecordMissAsync(key, Miss("thread-own-newer", true, OwnMarkedComment));
        await this.HarvestedAtAsync("thread-human", ReviewedAt);
        await this.HarvestedAtAsync("thread-own-older", ReviewedAt.AddMinutes(1));
        await this.HarvestedAtAsync("thread-own-newer", ReviewedAt.AddMinutes(2));

        var rows = await this._reader.ListMissesAsync(this.Query(ClientA) with { Limit = 1 });

        Assert.Equal("thread-human", Assert.Single(rows).ProviderThreadId);
    }

    [Fact]
    public async Task TheRowLimitStillBoundsWhatComesBack()
    {
        var key = await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs"]);
        await this._store.RecordMissAsync(key, Miss("thread-older", true));
        await this._store.RecordMissAsync(key, Miss("thread-newer", true));
        await this.HarvestedAtAsync("thread-older", ReviewedAt);
        await this.HarvestedAtAsync("thread-newer", ReviewedAt.AddMinutes(1));

        var rows = await this._reader.ListMissesAsync(this.Query(ClientA) with { Limit = 1 });

        Assert.Equal("thread-newer", Assert.Single(rows).ProviderThreadId);
    }

    [Fact]
    public async Task TheScanCeilingStopsTheEligibilityScanBeforeAnEligibleThreadBehindEnoughIneligibleOnes()
    {
        // The ceiling is set below the number of ProPR's own rows sitting in front of the one human thread, so
        // the scan gives up before reaching it. That thread does count toward recall on its own row, but the
        // scan ceiling still stops the page short of it, and the page comes back empty.
        const int ceiling = 3;
        var reader = new CodeInsightBrowseReader(
            this._dbContext,
            this._codec,
            TestPostedCommentComposer.Default,
            maxScannedMisses: ceiling);

        var key = await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs"]);
        await this._store.RecordMissAsync(key, Miss("thread-own-1", true, OwnMarkedComment));
        await this._store.RecordMissAsync(key, Miss("thread-own-2", true, OwnMarkedComment));
        await this._store.RecordMissAsync(key, Miss("thread-own-3", true, OwnMarkedComment));
        await this._store.RecordMissAsync(key, Miss("thread-human", true));
        await this.HarvestedAtAsync("thread-human", ReviewedAt);
        await this.HarvestedAtAsync("thread-own-1", ReviewedAt.AddMinutes(1));
        await this.HarvestedAtAsync("thread-own-2", ReviewedAt.AddMinutes(2));
        await this.HarvestedAtAsync("thread-own-3", ReviewedAt.AddMinutes(3));

        var rows = await reader.ListMissesAsync(this.Query(ClientA) with { Limit = 1 });

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ARaisedCeilingReachesTheSameEligibleThreadTheDefaultCeilingWouldMiss()
    {
        // Same shape as the row above, but with room to scan past the ineligible rows: this is what confirms
        // the previous test's empty result comes from the ceiling and not from some other reason the thread
        // was excluded.
        var reader = new CodeInsightBrowseReader(
            this._dbContext,
            this._codec,
            TestPostedCommentComposer.Default,
            maxScannedMisses: 10);

        var key = await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs"]);
        await this._store.RecordMissAsync(key, Miss("thread-own-1", true, OwnMarkedComment));
        await this._store.RecordMissAsync(key, Miss("thread-own-2", true, OwnMarkedComment));
        await this._store.RecordMissAsync(key, Miss("thread-own-3", true, OwnMarkedComment));
        await this._store.RecordMissAsync(key, Miss("thread-human", true));
        await this.HarvestedAtAsync("thread-human", ReviewedAt);
        await this.HarvestedAtAsync("thread-own-1", ReviewedAt.AddMinutes(1));
        await this.HarvestedAtAsync("thread-own-2", ReviewedAt.AddMinutes(2));
        await this.HarvestedAtAsync("thread-own-3", ReviewedAt.AddMinutes(3));

        var rows = await reader.ListMissesAsync(this.Query(ClientA) with { Limit = 1 });

        Assert.Equal("thread-human", Assert.Single(rows).ProviderThreadId);
    }

    [Fact]
    public async Task AnotherClientsHarvestedThreadsAreNeverReturned()
    {
        var mine = await this.SeedAsync(ClientA, "repo-1", 7, ["a.cs"]);
        var theirs = await this.SeedAsync(ClientB, "repo-1", 7, ["a.cs"]);
        await this._store.RecordMissAsync(mine, Miss("thread-1", true));
        await this._store.RecordMissAsync(theirs, Miss("thread-2", true));

        var rows = await this._reader.ListMissesAsync(this.Query(ClientA));

        Assert.Equal(ClientA, Assert.Single(rows).ClientId);
    }

    [Fact]
    public async Task NothingCollectedForTheScopeIsAnEmptyListRatherThanAFailure()
    {
        Assert.Empty(await this._reader.ListFindingsAsync(this.Query(ClientA)));
        Assert.Empty(await this._reader.ListMissesAsync(this.Query(ClientA)));
    }

    /// <summary>
    ///     Moves a stored miss to <paramref name="harvestedAt" />, so a test can say which row is the newest one
    ///     instead of depending on how close together two inserts landed.
    /// </summary>
    private async Task HarvestedAtAsync(string providerThreadId, DateTimeOffset harvestedAt)
    {
        var miss = await this._dbContext.CodeInsightMisses
            .SingleAsync(candidate => candidate.ProviderThreadId == providerThreadId);
        this._dbContext.Entry(miss).Property(nameof(CodeInsightMiss.HarvestedAt)).CurrentValue = harvestedAt;
        await this._dbContext.SaveChangesAsync();
    }

    private CodeInsightBrowseQuery Query(params Guid[] clientIds)
    {
        return new CodeInsightBrowseQuery(clientIds, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
    }

    private async Task<CodeInsightPullRequestKey> SeedAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        string[] files,
        DateTimeOffset? observedAt = null)
    {
        var jobId = Guid.NewGuid();
        var key = new CodeInsightPullRequestKey(clientId, repositoryId, pullRequestId);
        var snapshots = files
            .Select((file, ordinal) => new CodeInsightFindingSnapshot(
                ordinal,
                file,
                10 + ordinal,
                CommentSeverity.Error,
                $"Finding {ordinal} in {file}",
                "Baseline",
                null,
                null,
                false,
                ReviewCommentScopeRelation.OnChangedLine,
                null,
                $"thread-{jobId:N}-{ordinal}",
                $"comment-{jobId:N}-{ordinal}"))
            .ToList();

        await this._store.MaterialiseFindingsAsync(key, jobId, $"rev-{jobId:N}", observedAt ?? ReviewedAt, snapshots);
        return key;
    }

    private static CodeInsightMissRecord Miss(
        string providerThreadId,
        bool countsAsMiss,
        string discussion = "alice: this drops the retry count")
    {
        return new CodeInsightMissRecord(
            providerThreadId,
            "a.cs",
            42,
            discussion,
            IsSubstantive: countsAsMiss,
            WasActedOn: true,
            IsInScope: countsAsMiss,
            0.9,
            "test");
    }

    private static CodeInsightClassification Classification(string slug)
    {
        return new CodeInsightClassification(
            [slug],
            [],
            CodeInsightFindingLevel.Member,
            CodeInsightFindingQualifier.Incorrect,
            0.8,
            "test");
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
            $"MeisterDev.ProPR.CodeInsightBrowseReaderTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }
}
