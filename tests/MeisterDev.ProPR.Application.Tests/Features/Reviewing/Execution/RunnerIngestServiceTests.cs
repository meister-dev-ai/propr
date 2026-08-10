// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class RunnerIngestServiceTests
{
    private static readonly Guid JobId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly RunnerCallContext Call = new(JobId, 1, "runner-a");

    private readonly IRunnerCallAuthorizer _authorizer = Substitute.For<IRunnerCallAuthorizer>();
    private readonly IRunnerIngestLedger _ledger = Substitute.For<IRunnerIngestLedger>();
    private readonly IRunnerIngestWriter _writer = Substitute.For<IRunnerIngestWriter>();

    private static RunnerIngestBatch Batch(int sequence, string key = "batch", int events = 1)
    {
        return new RunnerIngestBatch(
            sequence,
            key,
            [
                .. Enumerable.Range(0, events).Select(i =>
                    new RunnerTraceEvent(DateTimeOffset.UtcNow, $"event_{i}", null))
            ],
            [],
            []);
    }

    private RunnerIngestService CreateService(
        bool authorized = true,
        int expectedSequence = 1,
        bool recordWins = true,
        int maxItems = 500)
    {
        this._authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(
                authorized
                    ? RunnerCallAuthorization.Allow(Guid.NewGuid())
                    : RunnerCallAuthorization.Refuse(RunnerCallRefusal.SupersededGeneration));
        this._ledger.GetExpectedSequenceAsync(JobId, Arg.Any<CancellationToken>()).Returns(expectedSequence);
        this._ledger.TryRecordAsync(JobId, Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(recordWins);

        return new RunnerIngestService(
            this._authorizer,
            this._ledger,
            this._writer,
            Microsoft.Extensions.Options.Options.Create(new RunnerIngestOptions { MaxItemsPerBatch = maxItems }));
    }

    [Fact]
    public async Task TheNextBatchInSequence_IsApplied()
    {
        var result = await this.CreateService(expectedSequence: 3).IngestAsync(Call, Batch(3));

        Assert.Equal(RunnerIngestOutcome.Applied, result.Outcome);
        Assert.Equal(4, result.ExpectedSequence);
        await this._writer.Received(1).WriteEventsAsync(JobId, Arg.Any<IReadOnlyList<RunnerTraceEvent>>(), Arg.Any<CancellationToken>());
    }

    // A resend after a network failure is the most ordinary thing that happens to an executor. Answering it
    // as an error would make every executor carry error handling for the normal case.
    [Fact]
    public async Task AResendOfAnAppliedBatch_IsSuccessWithNothingWrittenAgain()
    {
        var result = await this.CreateService(expectedSequence: 5).IngestAsync(Call, Batch(3));

        Assert.Equal(RunnerIngestOutcome.AlreadyApplied, result.Outcome);
        Assert.True(result.MaySpoolBeTrimmed);
        await this._writer.DidNotReceive().WriteEventsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunnerTraceEvent>>(), Arg.Any<CancellationToken>());
    }

    // Idempotency alone does not give ordering. A trace with a hole in it is not a trace anybody can read,
    // so the gap is named and the executor is told where to resume from.
    [Fact]
    public async Task ABatchThatSkipsAhead_IsRefusedAndTheGapIsNamed()
    {
        var result = await this.CreateService(expectedSequence: 4).IngestAsync(Call, Batch(7));

        Assert.Equal(RunnerIngestOutcome.OutOfOrder, result.Outcome);
        Assert.Equal(4, result.ExpectedSequence);
        Assert.False(result.MaySpoolBeTrimmed);
        await this._writer.DidNotReceive().WriteEventsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunnerTraceEvent>>(), Arg.Any<CancellationToken>());
    }

    // Two deliveries of the same batch arriving together both try to record it; the one that loses the
    // uniqueness race must not also write, or the trace doubles.
    [Fact]
    public async Task WhenTwoDeliveriesRace_OnlyTheWinnerWrites()
    {
        var result = await this.CreateService(recordWins: false).IngestAsync(Call, Batch(1));

        Assert.Equal(RunnerIngestOutcome.AlreadyApplied, result.Outcome);
        await this._writer.DidNotReceive().WriteEventsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunnerTraceEvent>>(), Arg.Any<CancellationToken>());
    }

    // Refused whole rather than partly applied: a half-applied batch leaves the executor unable to say what
    // still needs sending, which is exactly what the sequence exists to prevent.
    [Fact]
    public async Task AnOversizedBatch_IsRefusedWholeWithTheExpectedSequenceToResumeFrom()
    {
        var result = await this.CreateService(expectedSequence: 2, maxItems: 3)
            .IngestAsync(Call, Batch(2, events: 10));

        Assert.Equal(RunnerIngestOutcome.TooLarge, result.Outcome);
        Assert.Equal(2, result.ExpectedSequence);
        await this._writer.DidNotReceive().WriteEventsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunnerTraceEvent>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnauthorizedBatch_IsNotApplied()
    {
        var result = await this.CreateService(authorized: false).IngestAsync(Call, Batch(1));

        Assert.Equal(RunnerIngestOutcome.NotAuthorized, result.Outcome);
        Assert.Equal(RunnerCallRefusal.SupersededGeneration, result.CallRefusal);
        await this._ledger.DidNotReceive().TryRecordAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptySectionsOfABatch_AreNotWrittenAtAll()
    {
        var service = this.CreateService();

        await service.IngestAsync(
            Call,
            new RunnerIngestBatch(1, "k", [new RunnerTraceEvent(DateTimeOffset.UtcNow, "e", null)], [], []));

        await this._writer.DidNotReceive().WriteFileResultsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunnerFileOutcome>>(), Arg.Any<CancellationToken>());
        await this._writer.DidNotReceive().WriteSpendAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<RunnerSpendRecord>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFullBatch_WritesEventsResultsAndSpend()
    {
        var service = this.CreateService();
        var batch = new RunnerIngestBatch(
            1,
            "k",
            [new RunnerTraceEvent(DateTimeOffset.UtcNow, "e", null)],
            [new RunnerFileOutcome("src/a.cs", true, false, "done", null, ["1:m:-:-:None"])],
            [new RunnerSpendRecord("reviewer-medium", 100, 20, 0.5m)]);

        var result = await service.IngestAsync(Call, batch);

        Assert.Equal(RunnerIngestOutcome.Applied, result.Outcome);
        await this._writer.Received(1).WriteEventsAsync(JobId, Arg.Any<IReadOnlyList<RunnerTraceEvent>>(), Arg.Any<CancellationToken>());
        await this._writer.Received(1).WriteFileResultsAsync(JobId, Arg.Any<IReadOnlyList<RunnerFileOutcome>>(), Arg.Any<CancellationToken>());
        await this._writer.Received(1).WriteSpendAsync(JobId, Arg.Any<IReadOnlyList<RunnerSpendRecord>>(), Arg.Any<CancellationToken>());
    }
}
