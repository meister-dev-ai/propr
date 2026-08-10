// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     The ledger has to be tested against the real database: its whole mechanism is a unique index, and an
///     in-memory double would simply agree with whatever the code asked it.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class RunnerIngestLedgerTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private DbContextOptions<MeisterProPRDbContext> _options = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private RunnerIngestLedger _ledger = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(this._options);
        await this._dbContext.RunnerIngestReceipts.ExecuteDeleteAsync();
        this._ledger = new RunnerIngestLedger(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task AJobWithNothingApplied_ExpectsTheFirstBatch()
    {
        Assert.Equal(1, await this._ledger.GetExpectedSequenceAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task RecordingABatch_MovesTheExpectedSequenceOn()
    {
        var jobId = Guid.NewGuid();

        Assert.True(await this._ledger.TryRecordAsync(jobId, 1, "batch-1"));

        Assert.Equal(2, await this._ledger.GetExpectedSequenceAsync(jobId));
    }

    // The failure this guards against is a control-plane restart mid-review: everything the executor
    // resends afterwards would otherwise be applied a second time.
    [Fact]
    public async Task RecordsSurviveANewContext()
    {
        var jobId = Guid.NewGuid();
        await this._ledger.TryRecordAsync(jobId, 1, "batch-1");

        await using var freshContext = new MeisterProPRDbContext(this._options);
        var freshLedger = new RunnerIngestLedger(freshContext);

        Assert.Equal(2, await freshLedger.GetExpectedSequenceAsync(jobId));
        Assert.False(await freshLedger.TryRecordAsync(jobId, 1, "batch-1"));
    }

    [Fact]
    public async Task RecordingTheSameSequenceTwice_LosesTheSecondTime()
    {
        var jobId = Guid.NewGuid();

        Assert.True(await this._ledger.TryRecordAsync(jobId, 1, "batch-1"));
        Assert.False(await this._ledger.TryRecordAsync(jobId, 1, "batch-1-again"));
    }

    [Fact]
    public async Task RecordingTheSameKeyTwice_LosesTheSecondTime()
    {
        var jobId = Guid.NewGuid();

        Assert.True(await this._ledger.TryRecordAsync(jobId, 1, "same-key"));
        Assert.False(await this._ledger.TryRecordAsync(jobId, 2, "same-key"));
    }

    // Two deliveries of one batch arriving together is the case the index exists for.
    [Fact]
    public async Task TwoConcurrentRecordsOfOneBatch_ProduceExactlyOneWinner()
    {
        var jobId = Guid.NewGuid();
        await using var contextA = new MeisterProPRDbContext(this._options);
        await using var contextB = new MeisterProPRDbContext(this._options);

        var outcomes = await Task.WhenAll(
            new RunnerIngestLedger(contextA).TryRecordAsync(jobId, 1, "batch-1"),
            new RunnerIngestLedger(contextB).TryRecordAsync(jobId, 1, "batch-1"));

        Assert.Single(outcomes, won => won);
    }

    [Fact]
    public async Task OneJobsReceipts_DoNotAffectAnother()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await this._ledger.TryRecordAsync(first, 1, "batch-1");

        Assert.Equal(1, await this._ledger.GetExpectedSequenceAsync(second));
        Assert.True(await this._ledger.TryRecordAsync(second, 1, "batch-1"));
    }

    [Fact]
    public async Task ClearingAJob_RemovesOnlyItsOwnReceipts()
    {
        var kept = Guid.NewGuid();
        var cleared = Guid.NewGuid();
        await this._ledger.TryRecordAsync(kept, 1, "k");
        await this._ledger.TryRecordAsync(cleared, 1, "c");

        await this._ledger.ClearAsync(cleared);

        Assert.Equal(2, await this._ledger.GetExpectedSequenceAsync(kept));
        Assert.Equal(1, await this._ledger.GetExpectedSequenceAsync(cleared));
    }
}
