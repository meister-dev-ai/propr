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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.CodeInsights.Taxonomy;
using MeisterDev.ProPR.CodeInsights.Persistence;

namespace MeisterDev.ProPR.CodeInsights.Tests.Rollups;

/// <summary>
///     One stored grain, day-bucketed, with the five reporting grains and the wider buckets derived on read.
///     These tests pin the two properties that shape buys nothing without: the grains must reconcile, and
///     re-projecting must not inflate anything.
/// </summary>
public sealed class CodeInsightRollupTests : IDisposable
{
    private static readonly Guid ClientA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset ReviewedAt = new(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

    private readonly MeisterProPRDbContext _dbContext;
    private readonly CodeInsightFindingStore _store;
    private readonly CodeInsightRollupProjector _projector;
    private readonly CodeInsightRollupReader _reader;
    private readonly ICodeInsightsCollectionGate _gate;

    public CodeInsightRollupTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"CodeInsightRollupTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._store = new CodeInsightFindingStore(this._dbContext, CreateCodec());

        this._gate = Substitute.For<ICodeInsightsCollectionGate>();
        this._gate.IsCollectionEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        this._projector = new CodeInsightRollupProjector(
            this._dbContext,
            this._gate,
            NullLogger<CodeInsightRollupProjector>.Instance);
        this._reader = new CodeInsightRollupReader(this._dbContext);
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task TheGrainsReconcileAgainstEachOther()
    {
        // The whole justification for storing one grain is that the others derive from it. If they do not add
        // up, the shape is wrong and every chart built on it is wrong too.
        var jobId = Guid.NewGuid();
        await this.SeedAsync(jobId, ClientA, "repo-1", pullRequestId: 7, files: ["a.cs", "a.cs", "b.cs"]);

        var window = this.Window(ClientA);
        var clientTotal = await this._reader.GetTotalAsync(window);
        var byFile = await this._reader.GetConcentrationAsync(window, CodeInsightGrain.File, 10);
        var byRepo = await this._reader.GetConcentrationAsync(window, CodeInsightGrain.Repository, 10);
        var byPr = await this._reader.GetConcentrationAsync(window, CodeInsightGrain.PullRequest, 10);
        var byJob = await this._reader.GetConcentrationAsync(window, CodeInsightGrain.Job, 10);

        Assert.Equal(3, clientTotal);
        Assert.Equal(3, byFile.Sum(row => row.Count));
        Assert.Equal(3, byRepo.Sum(row => row.Count));
        Assert.Equal(3, byPr.Sum(row => row.Count));
        Assert.Equal(3, byJob.Sum(row => row.Count));
        // Two files, so the file grain splits where the coarser grains do not.
        Assert.Equal(2, byFile.Count);
        Assert.Single(byRepo);
    }

    [Fact]
    public async Task ReProjectingTheSameJobDoesNotInflateAnything()
    {
        // Three separate events feed these counts and a crawl can deliver any of them twice. Recomputation,
        // not increment, is what makes that safe.
        var jobId = Guid.NewGuid();
        await this.SeedAsync(jobId, ClientA, "repo-1", 7, ["a.cs", "b.cs"]);

        await this._projector.ProjectJobAsync(jobId);
        await this._projector.ProjectJobAsync(jobId);
        await this._projector.ProjectJobAsync(jobId);

        Assert.Equal(2, await this._reader.GetTotalAsync(this.Window(ClientA)));
    }

    [Fact]
    public async Task ClassifyingAFindingAfterTheFirstProjectionAddsItsTypeWithoutDoubleCountingTheFinding()
    {
        var jobId = Guid.NewGuid();
        var key = await this.SeedAsync(jobId, ClientA, "repo-1", 7, ["a.cs"]);

        var finding = (await this._store.GetFindingsForPullRequestAsync(key)).Single();
        await this._store.ApplyClassificationAsync(
            finding.Id,
            new CodeInsightClassification(
                ["logic-error", "security"],
                [],
                CodeInsightFindingLevel.Member,
                CodeInsightFindingQualifier.Missing,
                0.8,
                "test"));
        await this._projector.ProjectJobAsync(jobId);

        var window = this.Window(ClientA);
        var types = await this._reader.GetSeriesAsync(
            window,
            CodeInsightCountDimension.CoreType,
            CodeInsightBucketSize.Day);

        // The finding is still one finding, but it touches two types, which is what a type series measures.
        Assert.Equal(1, await this._reader.GetTotalAsync(window));
        Assert.Equal(2, types.Count);
        Assert.All(types, point => Assert.Equal(1, point.Count));
    }

    [Fact]
    public async Task ADispositionRecordedLaterLandsInTheReviewsBucketNotTodays()
    {
        // Otherwise a quality trend moves retroactively for reasons nobody can explain.
        var jobId = Guid.NewGuid();
        var key = await this.SeedAsync(jobId, ClientA, "repo-1", 7, ["a.cs"]);
        var finding = (await this._store.GetFindingsForPullRequestAsync(key)).Single();

        await this._store.RecordDispositionAsync(
            finding.Id,
            new CodeInsightDispositionRecord(
                CodeInsightDisposition.Addressed,
                ThreadResolutionIntent.ClaimsFix,
                ThreadAnchorCodeChange.Changed,
                null,
                null));
        await this._projector.ProjectJobAsync(jobId);

        var outcomes = await this._reader.GetSeriesAsync(
            this.Window(ClientA),
            CodeInsightCountDimension.Disposition,
            CodeInsightBucketSize.Day);

        var point = Assert.Single(outcomes);
        Assert.Equal(DateOnly.FromDateTime(ReviewedAt.UtcDateTime), point.BucketStart);
        Assert.Equal(nameof(CodeInsightDisposition.Addressed), point.DimensionKey);
    }

    [Fact]
    public async Task WeekAndMonthBucketsAgreeWithTheDayRowsTheyDeriveFrom()
    {
        var window = new CodeInsightRollupQuery(
            [ClientA],
            new DateOnly(2026, 3, 1),
            new DateOnly(2026, 3, 31));

        // Two reviews in the same ISO week, one later in the same month.
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 7, ["a.cs"], new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero));
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 8, ["b.cs"], new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero));
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 9, ["c.cs"], new DateTimeOffset(2026, 3, 25, 9, 0, 0, TimeSpan.Zero));

        var days = await this._reader.GetSeriesAsync(window, CodeInsightCountDimension.FindingTotal, CodeInsightBucketSize.Day);
        var weeks = await this._reader.GetSeriesAsync(window, CodeInsightCountDimension.FindingTotal, CodeInsightBucketSize.Week);
        var months = await this._reader.GetSeriesAsync(window, CodeInsightCountDimension.FindingTotal, CodeInsightBucketSize.Month);

        Assert.Equal(3, days.Count);
        Assert.Equal(2, weeks.Count);
        Assert.Single(months);
        Assert.Equal(days.Sum(point => point.Count), weeks.Sum(point => point.Count));
        Assert.Equal(days.Sum(point => point.Count), months.Sum(point => point.Count));
        // The 11th and 12th of March 2026 are Wednesday and Thursday of the week starting Monday the 9th.
        Assert.Equal(new DateOnly(2026, 3, 9), weeks[0].BucketStart);
        Assert.Equal(2, weeks[0].Count);
        Assert.Equal(new DateOnly(2026, 3, 1), months[0].BucketStart);
    }

    [Theory]
    [InlineData("2026-03-09", "2026-03-09")]
    [InlineData("2026-03-11", "2026-03-09")]
    [InlineData("2026-03-15", "2026-03-09")]
    [InlineData("2026-03-16", "2026-03-16")]
    public void AWeekIsAnchoredToItsMondayRegardlessOfLocale(string day, string expectedMonday)
    {
        var start = CodeInsightRollupReader.BucketStart(DateOnly.Parse(day), CodeInsightBucketSize.Week);

        Assert.Equal(DateOnly.Parse(expectedMonday), start);
    }

    [Fact]
    public async Task AReadNeverCrossesIntoAClientTheCallerDidNotSupply()
    {
        // The authorised set comes from the caller. A cross-client aggregate over an unchecked set would be an
        // exfiltration primitive, so this is asserted rather than assumed.
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 7, ["a.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientB, "repo-2", 8, ["b.cs", "c.cs"]);

        Assert.Equal(1, await this._reader.GetTotalAsync(this.Window(ClientA)));
        Assert.Equal(2, await this._reader.GetTotalAsync(this.Window(ClientB)));
        Assert.Equal(3, await this._reader.GetTotalAsync(this.Window(ClientA, ClientB)));
    }

    [Fact]
    public async Task AnEmptyAuthorisedSetYieldsNothingRatherThanEverything()
    {
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 7, ["a.cs"]);

        var window = new CodeInsightRollupQuery([], new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        Assert.Equal(0, await this._reader.GetTotalAsync(window));
        Assert.Empty(await this._reader.GetSeriesAsync(window, CodeInsightCountDimension.FindingTotal, CodeInsightBucketSize.Day));
        Assert.Empty(await this._reader.GetConcentrationAsync(window, CodeInsightGrain.Repository, 10));
    }

    [Fact]
    public async Task NoCustomTypeCanReachTheProjection()
    {
        // Two clients' identically-named custom tags are not the same thing, so they are excluded from the
        // projection entirely rather than filtered at read time and hoped for.
        var jobId = Guid.NewGuid();
        var key = await this.SeedAsync(jobId, ClientA, "repo-1", 7, ["a.cs"]);
        var customTag = new Domain.Entities.CodeInsightCustomTag
        {
            Id = Guid.CreateVersion7(),
            ClientId = ClientA,
            Slug = "domain-rule",
            DisplayName = "Domain rule",
            Definition = "Violates a business rule.",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        this._dbContext.CodeInsightCustomTags.Add(customTag);
        await this._dbContext.SaveChangesAsync();

        var finding = (await this._store.GetFindingsForPullRequestAsync(key)).Single();
        await this._store.ApplyClassificationAsync(
            finding.Id,
            new CodeInsightClassification(
                ["logic-error"],
                [customTag.Id],
                CodeInsightFindingLevel.Member,
                CodeInsightFindingQualifier.Missing,
                0.8,
                "test"));
        await this._projector.ProjectJobAsync(jobId);

        var types = await this._reader.GetSeriesAsync(
            this.Window(ClientA),
            CodeInsightCountDimension.CoreType,
            CodeInsightBucketSize.Day);

        Assert.Equal([CodeInsightCoreTaxonomy.LogicError], types.Select(point => point.DimensionKey).ToList());
        Assert.DoesNotContain("domain-rule", types.Select(point => point.DimensionKey));
    }

    [Fact]
    public async Task WithTheGateClosedNothingIsProjected()
    {
        var jobId = Guid.NewGuid();
        await this.SeedFindingsOnlyAsync(jobId, ClientA, "repo-1", 7, ["a.cs"]);
        this._gate.IsCollectionEnabledAsync(ClientA, Arg.Any<CancellationToken>()).Returns(false);

        await this._projector.ProjectJobAsync(jobId);

        Assert.Empty(await this._dbContext.CodeInsightDailyCounts.ToListAsync());
    }

    [Fact]
    public async Task AJobWhoseFindingsAreGoneHasItsStaleCellsRemoved()
    {
        // Retention purges findings; a projection left behind would keep reporting counts for data that no
        // longer exists.
        var jobId = Guid.NewGuid();
        var key = await this.SeedAsync(jobId, ClientA, "repo-1", 7, ["a.cs"]);
        Assert.NotEmpty(await this._dbContext.CodeInsightDailyCounts.ToListAsync());

        await this._store.PurgeForClientAsync(key.ClientId);
        await this._projector.ProjectJobAsync(jobId);

        Assert.Empty(await this._dbContext.CodeInsightDailyCounts.ToListAsync());
    }

    [Fact]
    public async Task ConcentrationRanksTheBusiestScopeFirstAndHonoursTopN()
    {
        await this.SeedAsync(Guid.NewGuid(), ClientA, "busy-repo", 7, ["a.cs", "b.cs", "c.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientA, "quiet-repo", 8, ["d.cs"]);

        var ranked = await this._reader.GetConcentrationAsync(this.Window(ClientA), CodeInsightGrain.Repository, 1);

        var top = Assert.Single(ranked);
        Assert.Equal("busy-repo", top.RepositoryId);
        Assert.Equal(3, top.Count);
    }

    [Fact]
    public async Task TheRepositoryDirectoryRanksByVolumeAndCarriesEachRepositorysOwnNumbers()
    {
        // What a reader picks from. The comparison it supports is where the findings are, and the per-repository
        // numbers exist so the choice is informed, not so two codebases can be scored against each other.
        await this.SeedAsync(Guid.NewGuid(), ClientA, "busy", 1, ["a.cs", "a.cs", "b.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientA, "busy", 2, ["c.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientA, "quiet", 3, ["d.cs"]);

        var directory = await this._reader.GetRepositoryDirectoryAsync(this.Window(ClientA));

        Assert.Equal(["busy", "quiet"], directory.Rows.Select(row => row.RepositoryId));

        var busy = directory.Rows[0];
        Assert.Equal(4, busy.Findings);
        Assert.Equal(2, busy.PullRequests);
        Assert.Equal(3, busy.Files);
        Assert.Equal(2d, busy.AveragePerPullRequest!.Value, 12);
        Assert.Equal(DateOnly.FromDateTime(ReviewedAt.UtcDateTime), busy.LastActivityOn);

        Assert.Equal(5, directory.TotalFindings);
        Assert.Equal(2, directory.Repositories);
        // Three distinct pull requests across both repositories, counted as (repository, pull request) pairs.
        Assert.Equal(3, directory.PullRequests);
    }

    [Fact]
    public async Task TheRepositoryDirectoryIgnoresARepositoryNarrowingBecauseItIsTheListOfAlternatives()
    {
        // Narrowing it to the current choice would hide the alternatives, which is the one thing this read is for.
        await this.SeedAsync(Guid.NewGuid(), ClientA, "busy", 1, ["a.cs", "b.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientA, "quiet", 2, ["c.cs"]);

        var narrowed = this.Window(ClientA) with { RepositoryId = "busy" };
        var directory = await this._reader.GetRepositoryDirectoryAsync(narrowed);

        Assert.Equal(2, directory.Repositories);
    }

    [Fact]
    public async Task TheRepositoryDirectoryCountsPullRequestLevelFindingsButNotAsFiles()
    {
        // A finding about the pull request as a whole is in no file, and calling it one would inflate every
        // repository's file count by the reviews that had something to say about the change itself.
        await this.SeedAsync(Guid.NewGuid(), ClientA, "busy", 1, ["a.cs", ""]);

        var row = Assert.Single((await this._reader.GetRepositoryDirectoryAsync(this.Window(ClientA))).Rows);

        Assert.Equal(2, row.Findings);
        Assert.Equal(1, row.Files);
    }

    [Fact]
    public async Task TheRepositoryDirectoryNeverReadsAcrossClientsTheCallerDidNotSupply()
    {
        await this.SeedAsync(Guid.NewGuid(), ClientA, "mine", 1, ["a.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientB, "theirs", 2, ["b.cs"]);

        Assert.Equal("mine", Assert.Single((await this._reader.GetRepositoryDirectoryAsync(this.Window(ClientA))).Rows).RepositoryId);
        Assert.Empty(
            (await this._reader.GetRepositoryDirectoryAsync(new CodeInsightRollupQuery([], new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)))).Rows);
    }

    [Fact]
    public async Task HotspotsCountAFilesWholeHistoryAndAverageItOverThePullRequestsThatFoundSomething()
    {
        // Three pull requests touch a.cs; two of them find something there. The average is over those two, which
        // is the only denominator the collection can see.
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 1, ["a.cs", "a.cs", "b.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 2, ["a.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 3, ["b.cs"]);

        var report = await this._reader.GetHotspotsAsync(this.Window(ClientA), null, 10);

        var worst = report.Files[0];
        Assert.Equal("a.cs", worst.FilePath);
        Assert.Equal(3, worst.Findings);
        Assert.Equal(2, worst.PullRequests);
        Assert.Equal(1.5d, worst.AveragePerPullRequest!.Value, 12);

        // The totals describe the whole scope, and the pull-request count is distinct across files rather than summed.
        Assert.Equal(5, report.TotalFindings);
        Assert.Equal(3, report.PullRequests);
        Assert.Equal(5d / 3d, report.AveragePerPullRequest!.Value, 12);
        Assert.Equal(2, report.FileCount);
    }

    [Fact]
    public async Task HotspotsCanGroupByTheDefinitionAFindingSitsIn()
    {
        // The reason findings record a symbol: "which part of this file keeps producing findings", not just which file.
        await this.SeedAsync(
            Guid.NewGuid(), ClientA, "repo-1", 1, ["a.cs", "a.cs", "a.cs"],
            symbols: ["Process", "Process", "Validate"]);
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 2, ["a.cs"], symbols: ["Process"]);

        var report = await this._reader.GetHotspotsAsync(this.Window(ClientA), null, 10, CodeInsightHotspotGrouping.Symbol);

        var worst = report.Files[0];
        Assert.Equal("Process", worst.SymbolName);
        Assert.Equal("a.cs", worst.FilePath);
        Assert.Equal(3, worst.Findings);
        Assert.Equal(2, worst.PullRequests);
        Assert.Equal(1.5d, worst.AveragePerPullRequest!.Value, 12);

        Assert.Equal("Validate", report.Files[1].SymbolName);
        Assert.Equal(2, report.FileCount);
    }

    [Fact]
    public async Task HotspotsBySymbolReportWhatTheSyntaxCouldNotPlaceRatherThanRankingItAsABucket()
    {
        // An "(unknown)" row would rank as if it were somewhere in the code. The count is stated instead.
        await this.SeedAsync(
            Guid.NewGuid(), ClientA, "repo-1", 1, ["a.cs", "a.cs", "b.cs"],
            symbols: ["Process", null, null]);

        var report = await this._reader.GetHotspotsAsync(this.Window(ClientA), null, 10, CodeInsightHotspotGrouping.Symbol);

        Assert.Equal("Process", Assert.Single(report.Files).SymbolName);
        Assert.Equal(1, report.TotalFindings);
        Assert.Equal(2, report.UnplacedFindings);
    }

    [Fact]
    public async Task HotspotsByFileNeverReportAnythingAsUnplaced()
    {
        // Every finding has a file, including the pull-request-level ones, whose file is the empty string.
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 1, ["a.cs", "b.cs"], symbols: [null, null]);

        var report = await this._reader.GetHotspotsAsync(this.Window(ClientA), null, 10);

        Assert.Equal(0, report.UnplacedFindings);
        Assert.Equal(2, report.TotalFindings);
    }

    [Fact]
    public async Task HotspotsBySymbolKeepTwoFilesSharingOneNameApart()
    {
        // The name is name-based, so the file is what disambiguates it: one row each, never one row of four.
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 1, ["a.cs", "a.cs"], symbols: ["Handle", "Handle"]);
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 2, ["b.cs", "b.cs"], symbols: ["Handle", "Handle"]);

        var report = await this._reader.GetHotspotsAsync(this.Window(ClientA), null, 10, CodeInsightHotspotGrouping.Symbol);

        Assert.Equal(2, report.Files.Count);
        Assert.All(report.Files, row => Assert.Equal("Handle", row.SymbolName));
        Assert.Equal(["a.cs", "b.cs"], report.Files.Select(row => row.FilePath).Order());
    }

    [Fact]
    public async Task HotspotsReportTheWholeScopeEvenWhenTheRankingIsTruncated()
    {
        // A ranked list of one must not make a codebase look like a codebase with one file in it.
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 1, ["a.cs", "a.cs", "b.cs", "c.cs"]);

        var report = await this._reader.GetHotspotsAsync(this.Window(ClientA), null, 1);

        Assert.Single(report.Files);
        Assert.Equal(4, report.TotalFindings);
        Assert.Equal(3, report.FileCount);
    }

    [Fact]
    public async Task HotspotsForOnePullRequestsFilesStillCountEveryPullRequest()
    {
        // The point of the view embedded in a review: these files, all of their history. A pull request chooses
        // the files and never the findings.
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 1, ["touched.cs", "touched.cs", "elsewhere.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 2, ["touched.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 3, ["elsewhere.cs", "elsewhere.cs"]);

        var report = await this._reader.GetHotspotsAsync(this.Window(ClientA), filesFromPullRequestId: 2, topN: 10);

        // Only the file pull request 2 found something in, but with all three of its findings across two pull requests.
        var only = Assert.Single(report.Files);
        Assert.Equal("touched.cs", only.FilePath);
        Assert.Equal(3, only.Findings);
        Assert.Equal(2, only.PullRequests);
        Assert.Equal(3, report.TotalFindings);
    }

    [Fact]
    public async Task HotspotsIgnoreAPullRequestFilterOnTheQueryRatherThanReportingOnePullRequestAsHistory()
    {
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 1, ["a.cs", "a.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 2, ["a.cs"]);

        var narrowed = this.Window(ClientA) with { PullRequestId = 1 };
        var report = await this._reader.GetHotspotsAsync(narrowed, null, 10);

        // Three findings across both pull requests, not the two inside the one the query named.
        Assert.Equal(3, Assert.Single(report.Files).Findings);
        Assert.Equal(2, report.PullRequests);
    }

    [Fact]
    public async Task HotspotsForAPullRequestWithNoCollectedFindingsAreEmptyRatherThanTheWholeRepository()
    {
        // Falling back to every file would answer a question about this pull request with the codebase's worst.
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 1, ["a.cs"]);

        var report = await this._reader.GetHotspotsAsync(this.Window(ClientA), filesFromPullRequestId: 999, topN: 10);

        Assert.Empty(report.Files);
        Assert.Equal(0, report.TotalFindings);
        Assert.Null(report.AveragePerPullRequest);
    }

    [Fact]
    public async Task HotspotsNeverReadAcrossClientsTheCallerDidNotSupply()
    {
        await this.SeedAsync(Guid.NewGuid(), ClientA, "repo-1", 1, ["a.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientB, "repo-2", 2, ["a.cs", "a.cs"]);

        Assert.Equal(1, (await this._reader.GetHotspotsAsync(this.Window(ClientA), null, 10)).TotalFindings);
        Assert.Empty(
            (await this._reader.GetHotspotsAsync(
                new CodeInsightRollupQuery([], new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)),
                null,
                10)).Files);
    }

    [Fact]
    public async Task ConcentrationNamesTheRepositoryWhenAReviewReportedOneAndFallsBackToTheIdentifierOtherwise()
    {
        // The counts key on the provider's repository identifier, which for several providers is a bare number. A
        // ranked list of numbers is not a ranking anybody can act on, so the recorded display name travels with it.
        await this.SeedAsync(Guid.NewGuid(), ClientA, "4", 7, ["a.cs", "b.cs"]);
        await this.SeedAsync(Guid.NewGuid(), ClientA, "9", 8, ["c.cs"]);
        await this._store.TouchPullRequestAsync(
            new CodeInsightPullRequestKey(ClientA, "4", 7),
            "Active",
            ReviewedAt,
            "payments-api");

        var ranked = await this._reader.GetConcentrationAsync(this.Window(ClientA), CodeInsightGrain.Repository, 10);

        Assert.Equal("payments-api", ranked.Single(row => row.RepositoryId == "4").RepositoryName);
        // No review has told us a name for this one; null leaves the caller showing the identifier rather than a blank.
        Assert.Null(ranked.Single(row => row.RepositoryId == "9").RepositoryName);
    }

    private CodeInsightRollupQuery Window(params Guid[] clientIds)
    {
        return new CodeInsightRollupQuery(
            clientIds,
            new DateOnly(2026, 1, 1),
            new DateOnly(2026, 12, 31));
    }

    private async Task<CodeInsightPullRequestKey> SeedAsync(
        Guid jobId,
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        string[] files,
        DateTimeOffset? observedAt = null,
        string?[]? symbols = null)
    {
        var key = await this.SeedFindingsOnlyAsync(jobId, clientId, repositoryId, pullRequestId, files, observedAt, symbols);
        await this._projector.ProjectJobAsync(jobId);
        return key;
    }

    private async Task<CodeInsightPullRequestKey> SeedFindingsOnlyAsync(
        Guid jobId,
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        string[] files,
        DateTimeOffset? observedAt = null,
        string?[]? symbols = null)
    {
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
                $"comment-{jobId:N}-{ordinal}",
                null,
                null,
                symbols is not null && ordinal < symbols.Length ? symbols[ordinal] : null,
                symbols is not null && ordinal < symbols.Length && symbols[ordinal] is not null ? "Method" : null))
            .ToList();

        await this._store.MaterialiseFindingsAsync(
            key,
            jobId,
            $"rev-{jobId:N}",
            observedAt ?? ReviewedAt,
            snapshots);

        return key;
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterDev.ProPR.CodeInsightRollupTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterDev.ProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }
}
