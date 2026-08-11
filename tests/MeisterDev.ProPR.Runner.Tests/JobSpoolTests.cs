// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     What the spool must never do: lose what it was given. Everything a review produces on its way back to
///     the control plane passes through here, and a batch dropped on a transport failure leaves gaps in the
///     trace and a file result the control plane never records.
/// </summary>
public sealed class JobSpoolTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task AnEmptySpool_ShipsNothing()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var spool = CreateSpool(handler);

        Assert.True(await spool.FlushAsync(CancellationToken.None));
        Assert.Empty(handler.Batches);
    }

    [Fact]
    public async Task AFlush_ShipsEverythingBufferedInOneBatch()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var spool = CreateSpool(handler);

        spool.Add("protocol.begin", "{}", DateTimeOffset.UtcNow);
        spool.Add(new RunnerFileOutcome("a.cs", true, false, "ok", null, ["pass-1"]));
        spool.Add(new RunnerSpendRecord("reviewer", 100, 20, null));

        Assert.True(await spool.FlushAsync(CancellationToken.None));

        var batch = Assert.Single(handler.Batches);
        Assert.Equal(1, batch.GetProperty("events").GetArrayLength());
        Assert.Equal(1, batch.GetProperty("fileResults").GetArrayLength());
        Assert.Equal(1, batch.GetProperty("spend").GetArrayLength());
    }

    // The property the whole design rests on. A flush that discarded its batch on a network failure
    // would lose exactly the trace an operator needs to understand why the review was interrupted.
    [Fact]
    public async Task AFailedFlush_KeepsEverythingForTheNextAttempt()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK) { Throw = true };
        var spool = CreateSpool(handler);

        spool.Add("protocol.begin", "{}", DateTimeOffset.UtcNow);
        spool.Add(new RunnerFileOutcome("a.cs", true, false, "ok", null, []));

        Assert.False(await spool.FlushAsync(CancellationToken.None));

        handler.Throw = false;
        Assert.True(await spool.FlushAsync(CancellationToken.None));

        var batch = Assert.Single(handler.Batches);
        Assert.Equal(1, batch.GetProperty("events").GetArrayLength());
        Assert.Equal(1, batch.GetProperty("fileResults").GetArrayLength());
    }

    // A refusal differs from a transport failure, but it is not an acknowledgement either, so the batch is
    // kept for the same reason.
    [Fact]
    public async Task ARefusedFlush_AlsoKeepsItsBatch()
    {
        var handler = new CapturingHandler(HttpStatusCode.Conflict);
        var spool = CreateSpool(handler);
        spool.Add("protocol.begin", "{}", DateTimeOffset.UtcNow);

        Assert.False(await spool.FlushAsync(CancellationToken.None));

        handler.Status = HttpStatusCode.OK;
        Assert.True(await spool.FlushAsync(CancellationToken.None));
        Assert.Equal(1, handler.Batches[^1].GetProperty("events").GetArrayLength());
    }

    // Order survives a retry. The events are a narrative, and one replayed out of order reads as a
    // different review than the one that happened.
    [Fact]
    public async Task ARetryAfterFailure_PreservesTheOrderEventsHappenedIn()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK) { Throw = true };
        var spool = CreateSpool(handler);

        spool.Add("first", "{}", DateTimeOffset.UtcNow);
        spool.Add("second", "{}", DateTimeOffset.UtcNow);
        await spool.FlushAsync(CancellationToken.None);

        spool.Add("third", "{}", DateTimeOffset.UtcNow);
        handler.Throw = false;
        await spool.FlushAsync(CancellationToken.None);

        var names = handler.Batches[^1].GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToArray();

        Assert.Equal(["first", "second", "third"], names);
    }

    // The idempotency key is derived from the sequence, so a batch resent after a timeout is recognised
    // as the same batch rather than counted twice. A random key per attempt would double-count spend.
    [Fact]
    public async Task EachBatch_CarriesAKeyDerivedFromItsSequence()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var spool = CreateSpool(handler);

        spool.Add("first", "{}", DateTimeOffset.UtcNow);
        await spool.FlushAsync(CancellationToken.None);
        spool.Add("second", "{}", DateTimeOffset.UtcNow);
        await spool.FlushAsync(CancellationToken.None);

        Assert.Equal($"{JobId:N}-1", handler.Batches[0].GetProperty("idempotencyKey").GetString());
        Assert.Equal($"{JobId:N}-2", handler.Batches[1].GetProperty("idempotencyKey").GetString());
        Assert.Equal(1, handler.Batches[0].GetProperty("sequence").GetInt32());
        Assert.Equal(2, handler.Batches[1].GetProperty("sequence").GetInt32());
    }

    // The defect this class exists to prevent: the ledger demands contiguous sequences, so a batch that
    // failed in transit has to be resent under its own number. Incrementing per attempt leaves a permanent
    // gap, and every later batch is refused as out of order for the rest of the job.
    [Fact]
    public async Task ABatchResentAfterAFailure_CarriesTheSameSequence()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK) { Throw = true };
        var spool = CreateSpool(handler);
        spool.Add("first", "{}", DateTimeOffset.UtcNow);

        Assert.False(await spool.FlushAsync(CancellationToken.None));

        handler.Throw = false;
        Assert.True(await spool.FlushAsync(CancellationToken.None));

        Assert.Equal(1, handler.Batches[^1].GetProperty("sequence").GetInt32());
        Assert.Equal($"{JobId:N}-1", handler.Batches[^1].GetProperty("idempotencyKey").GetString());
    }

    // And the batch after it continues from there rather than skipping the number the failure burned.
    [Fact]
    public async Task TheBatchAfterARetry_ContinuesTheSequenceWithoutAGap()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK) { Throw = true };
        var spool = CreateSpool(handler);
        spool.Add("first", "{}", DateTimeOffset.UtcNow);
        await spool.FlushAsync(CancellationToken.None);

        handler.Throw = false;
        await spool.FlushAsync(CancellationToken.None);
        spool.Add("second", "{}", DateTimeOffset.UtcNow);
        await spool.FlushAsync(CancellationToken.None);

        Assert.Equal([1, 2], handler.Batches.Select(b => b.GetProperty("sequence").GetInt32()).ToArray());
    }

    // The expected sequence is the whole backpressure contract, and it was being thrown away. A ledger
    // further ahead than the spool recorded tells it where to resume.
    [Fact]
    public async Task AnOutOfOrderRefusal_ResumesFromTheSequenceTheLedgerAsksFor()
    {
        var handler = new CapturingHandler(HttpStatusCode.Conflict)
        {
            ResponseBody = """{"outcome":"OutOfOrder","expectedSequence":7}""",
        };
        var spool = CreateSpool(handler);
        spool.Add("first", "{}", DateTimeOffset.UtcNow);

        Assert.False(await spool.FlushAsync(CancellationToken.None));

        handler.Status = HttpStatusCode.OK;
        handler.ResponseBody = null;
        Assert.True(await spool.FlushAsync(CancellationToken.None));

        Assert.Equal(7, handler.Batches[^1].GetProperty("sequence").GetInt32());
    }

    // A lease refusal wears the same 409 as an out-of-order batch but means something else entirely:
    // the job is not ours any more, so resending is a loop whose answer will not change.
    [Fact]
    public async Task ALeaseRefusal_StopsTheSpoolInsteadOfRetryingForever()
    {
        var handler = new CapturingHandler(HttpStatusCode.Conflict)
        {
            ResponseBody = """{"code":"lease_not_held","message":"This job is not held open by this replica."}""",
        };
        var spool = CreateSpool(handler);
        spool.Add("first", "{}", DateTimeOffset.UtcNow);

        Assert.False(await spool.FlushAsync(CancellationToken.None));

        Assert.True(spool.RefusedForGood);
    }

    // An out-of-order refusal is not the job being lost, so the spool keeps going.
    [Fact]
    public async Task AnOutOfOrderRefusal_DoesNotStopTheSpool()
    {
        var handler = new CapturingHandler(HttpStatusCode.Conflict)
        {
            ResponseBody = """{"outcome":"OutOfOrder","expectedSequence":2}""",
        };
        var spool = CreateSpool(handler);
        spool.Add("first", "{}", DateTimeOffset.UtcNow);

        await spool.FlushAsync(CancellationToken.None);

        Assert.False(spool.RefusedForGood);
    }

    // The class documented a bound it did not have. A long outage must not turn into memory exhaustion,
    // and what gives way is the trace rather than the review's output or its cost.
    [Fact]
    public async Task PastItsCeiling_TheSpoolDropsTraceEventsAndKeepsResultsAndSpend()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK) { Throw = true };
        var spool = CreateSpool(handler);

        for (var i = 0; i < 5200; i++)
        {
            spool.Add($"event-{i}", "{}", DateTimeOffset.UtcNow);
        }

        spool.Add(new RunnerFileOutcome("a.cs", true, false, "ok", null, []));
        spool.Add(new RunnerSpendRecord("reviewer", 100, 20, null));

        Assert.False(await spool.FlushAsync(CancellationToken.None));
        Assert.True(spool.DroppedEvents > 0);

        handler.Throw = false;
        Assert.True(await spool.FlushAsync(CancellationToken.None));

        var batch = handler.Batches[^1];
        Assert.Equal(1, batch.GetProperty("fileResults").GetArrayLength());
        Assert.Equal(1, batch.GetProperty("spend").GetArrayLength());
        Assert.True(batch.GetProperty("events").GetArrayLength() <= 5000);
    }

    [Fact]
    public void TheSpool_AsksToBeFlushedOnceEnoughHasAccumulated()
    {
        var spool = CreateSpool(new CapturingHandler(HttpStatusCode.OK));

        Assert.False(spool.ShouldFlush);
        for (var i = 0; i < 50; i++)
        {
            spool.Add($"event-{i}", "{}", DateTimeOffset.UtcNow);
        }

        Assert.True(spool.ShouldFlush);
    }

    // The store is the pipeline's persistence on the runner, and what it buffers is what the control
    // plane's rows will say. Findings and exclusion state that stay behind here are findings a reclaimed
    // job's synthesis never sees again.
    [Fact]
    public async Task ABufferedFileResult_CarriesItsFindingsAndExclusionOnTheWire()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var spool = CreateSpool(handler);
        var job = new ReviewJob(JobId, Guid.NewGuid(), "https://host.invalid", "proj", "repo", 12, 1);
        var store = new SpoolingFileResultStore(job, spool);

        var completed = new ReviewFileResult(JobId, "src/a.cs");
        completed.MarkCompleted("looks fine", [new ReviewComment("src/a.cs", 3, CommentSeverity.Warning, "Bounded?")], ["pass-1"]);
        await store.AddFileResultAsync(completed);
        var excluded = new ReviewFileResult(JobId, "src/generated.cs");
        excluded.MarkExcluded("**/generated/**");
        await store.AddFileResultAsync(excluded);

        Assert.True(await spool.FlushAsync(CancellationToken.None));

        var results = handler.Batches[^1].GetProperty("fileResults");
        var comment = Assert.Single(results[0].GetProperty("comments").EnumerateArray().ToList());
        Assert.Equal("Bounded?", comment.GetProperty("message").GetString());
        Assert.True(results[1].GetProperty("isExcluded").GetBoolean());
        Assert.Equal("**/generated/**", results[1].GetProperty("exclusionReason").GetString());
    }

    private static JobSpool CreateSpool(CapturingHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://control-plane.invalid/") };
        return new JobSpool(http, JobId, 1, NullLogger<JobSpool>.Instance);
    }

    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;

        public bool Throw { get; set; }

        /// <summary>What a refusal answers with, so a test can be a real 409 rather than a bare status.</summary>
        public string? ResponseBody { get; set; }

        public List<JsonElement> Batches { get; } = [];

        /// <summary>Every attempt, accepted or not. The tests assert on what a refused batch carries.</summary>
        public List<JsonElement> Attempts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (this.Throw)
            {
                throw new HttpRequestException("connection refused");
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            this.Attempts.Add(JsonDocument.Parse(body).RootElement.Clone());
            if (this.Status == HttpStatusCode.OK)
            {
                this.Batches.Add(JsonDocument.Parse(body).RootElement.Clone());
            }

            var response = new HttpResponseMessage(this.Status);
            if (this.ResponseBody is not null)
            {
                response.Content = new StringContent(this.ResponseBody, System.Text.Encoding.UTF8, "application/json");
            }

            return response;
        }
    }
}
