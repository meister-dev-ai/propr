// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.ThreadMemory;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.ThreadMemory;

/// <summary>
///     The keyword backlog on memories stored before extraction existed. Every row costs a model call, so what
///     matters here is that the sweep stays bounded, gated, and additive.
/// </summary>
public sealed class CodeInsightMemoryKeywordSweeperTests : IDisposable
{
    private static readonly Guid ClientA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid ClientB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly MeisterProPRDbContext _dbContext;
    private readonly IMemoryKeywordExtractor _extractor;
    private readonly ThreadMemoryKeywordSweeper _sweeper;

    public CodeInsightMemoryKeywordSweeperTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"CodeInsightMemoryKeywordTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        this._extractor = Substitute.For<IMemoryKeywordExtractor>();
        this._extractor
            .ExtractAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(["retry", "timeout"]);


        this._sweeper = new ThreadMemoryKeywordSweeper(
            this._dbContext,
            this._extractor,
            NullLogger<ThreadMemoryKeywordSweeper>.Instance);
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task AMemoryWithoutKeywordsGetsThemAndNothingElseChanges()
    {
        var before = await this.SeedAsync(ClientA, 1);

        Assert.Equal(1, await this._sweeper.SweepAsync(10));

        var after = await this._dbContext.ThreadMemoryRecords.SingleAsync();
        Assert.Equal(["retry", "timeout"], after.Keywords);
        // Additive only: the memory's own substance is untouched.
        Assert.Equal(before.ResolutionSummary, after.ResolutionSummary);
        Assert.Equal(before.ChangeExcerpt, after.ChangeExcerpt);
    }

    [Fact]
    public async Task AMemoryThatAlreadyHasKeywordsIsNotPaidForAgain()
    {
        var record = await this.SeedAsync(ClientA, 1);
        record.Keywords = ["existing"];
        await this._dbContext.SaveChangesAsync();

        Assert.Equal(0, await this._sweeper.SweepAsync(10));

        await this._extractor.DidNotReceive().ExtractAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheSweepIsBoundedAndResumes()
    {
        await this.SeedAsync(ClientA, 1);
        await this.SeedAsync(ClientA, 2);
        await this.SeedAsync(ClientA, 3);

        Assert.Equal(2, await this._sweeper.SweepAsync(2));
        Assert.Equal(1, await this._sweeper.SweepAsync(2));
        Assert.Equal(0, await this._sweeper.SweepAsync(2));
    }

    [Fact]
    public async Task AnEmptyExtractionLeavesTheRowAsACandidate()
    {
        // Right for a transient failure, and a bounded cost for a permanent one.
        await this.SeedAsync(ClientA, 1);
        this._extractor
            .ExtractAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        Assert.Equal(0, await this._sweeper.SweepAsync(10));

        Assert.Empty((await this._dbContext.ThreadMemoryRecords.SingleAsync()).Keywords);
    }

    [Fact]
    public async Task AFailingExtractionIsSwallowedAndTheMemorySurvives()
    {
        await this.SeedAsync(ClientA, 1);
        this._extractor
            .ExtractAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the model is unavailable"));

        Assert.Equal(0, await this._sweeper.SweepAsync(10));

        Assert.Single(await this._dbContext.ThreadMemoryRecords.ToListAsync());
    }

    [Fact]
    public async Task NothingToDoIsNotAModelCall()
    {
        Assert.Equal(0, await this._sweeper.SweepAsync(10));

        await this._extractor.DidNotReceive().ExtractAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    private async Task<ThreadMemoryRecord> SeedAsync(Guid clientId, long threadId)
    {
        var record = new ThreadMemoryRecord
        {
            Id = Guid.CreateVersion7(),
            ClientId = clientId,
            ThreadId = threadId,
            RepositoryId = "repo-1",
            PullRequestId = 7,
            FilePath = "src/Service.cs",
            ResolutionSummary = "The retry count was restored.",
            ChangeExcerpt = "retryCount = 3;",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-threadId),
        };

        this._dbContext.ThreadMemoryRecords.Add(record);
        await this._dbContext.SaveChangesAsync();
        return record;
    }
}
