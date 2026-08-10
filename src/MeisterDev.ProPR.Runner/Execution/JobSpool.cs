// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     Everything one job produces on its way back to the control plane, held until it can be shipped.
///     <para>
///         A review emits far more than its findings: a trace event per stage, a result per file, and a
///         spend record per completion. Sending each one as it happens would make a review's latency the
///         sum of its network round trips, and would lose whatever was in flight when the control plane
///         blinked. Batching is what makes a blip survivable.
///     </para>
///     <para>
///         Bounded on purpose. An unbounded buffer turns a long outage into memory exhaustion, so past the
///         ceiling the oldest trace events are dropped and counted. File results and spend are never
///         dropped: they are the review's output and its cost, where a trace event is a description of how
///         it got there. The count is reported so a trace with holes says so rather than looking complete.
///     </para>
///     <para>
///         The sequence advances on acknowledgement, not on attempt. The ledger requires contiguity, so a
///         batch that failed in transit must be resent under its own number — incrementing per attempt
///         leaves a permanent gap, and every later batch is refused as out of order for the rest of the
///         job.
///     </para>
/// </summary>
public sealed partial class JobSpool(
    HttpClient http,
    Guid jobId,
    int leaseGeneration,
    ILogger<JobSpool> logger)
{
    /// <summary>
    ///     How many buffered items trigger a flush. Small enough that an outage loses little, large enough
    ///     that a file-by-file review is not one request per stage.
    /// </summary>
    private const int FlushThreshold = 50;

    private readonly ConcurrentQueue<RunnerTraceEvent> _events = new();
    private readonly ConcurrentQueue<RunnerFileOutcome> _fileResults = new();
    private readonly ConcurrentQueue<RunnerSpendRecord> _spend = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    /// <summary>
    ///     How many buffered items may accumulate before the oldest trace events are dropped. Generous,
    ///     because the ordinary case is a blip; the ceiling is here so a long outage cannot exhaust memory.
    /// </summary>
    private const int MaxBufferedItems = 5000;

    /// <summary>The last sequence the control plane acknowledged. The next batch is this plus one.</summary>
    private int _acknowledgedSequence;

    /// <summary>Trace events dropped at the ceiling, so a shortened trace can say so.</summary>
    private int _droppedEvents;

    /// <summary>
    ///     Set when the control plane says this job is no longer ours to write to. Retrying then is not
    ///     backpressure, it is a loop: the answer will not change, and every attempt costs a round trip.
    /// </summary>
    private bool _refusedForGood;

    /// <summary>Buffers one trace event.</summary>
    /// <param name="name">Event name, matching what the in-process path records.</param>
    /// <param name="details">Structured detail, already serialised.</param>
    /// <param name="occurredAt">When it happened.</param>
    public void Add(string name, string? details, DateTimeOffset occurredAt)
    {
        this._events.Enqueue(new RunnerTraceEvent(occurredAt, name, details));
    }

    /// <summary>Buffers one file's outcome, which is also this job's resume checkpoint.</summary>
    /// <param name="outcome">The outcome.</param>
    public void Add(RunnerFileOutcome outcome)
    {
        this._fileResults.Enqueue(outcome);
    }

    /// <summary>Buffers what one relayed completion cost.</summary>
    /// <param name="spend">The spend record.</param>
    public void Add(RunnerSpendRecord spend)
    {
        this._spend.Enqueue(spend);
    }

    /// <summary>Whether enough has accumulated to be worth a round trip.</summary>
    public bool ShouldFlush =>
        this._events.Count + this._fileResults.Count + this._spend.Count >= FlushThreshold;

    /// <summary>
    ///     Whether the control plane has refused this job for good, so there is nothing left to flush to.
    /// </summary>
    public bool RefusedForGood => this._refusedForGood;

    /// <summary>Trace events dropped because the buffer reached its ceiling.</summary>
    public int DroppedEvents => this._droppedEvents;

    /// <summary>The job whose output this spool carries.</summary>
    public Guid JobId => jobId;

    /// <summary>
    ///     Ships everything buffered, in one batch, and keeps it buffered if the ship fails.
    ///     <para>
    ///         Items are only dropped once the control plane has acknowledged them. A flush that throws
    ///         away its batch on a transport failure would lose exactly the trace an operator needs to
    ///         understand why the review was interrupted.
    ///     </para>
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    public async Task<bool> FlushAsync(CancellationToken ct)
    {
        await this._flushGate.WaitAsync(ct);
        try
        {
            var events = Drain(this._events);
            var files = Drain(this._fileResults);
            var spend = Drain(this._spend);

            if (events.Count == 0 && files.Count == 0 && spend.Count == 0)
            {
                return true;
            }

            // The next number after the last one acknowledged, not the next after the last one attempted.
            var sequence = this._acknowledgedSequence + 1;
            var request = new
            {
                jobId,
                leaseGeneration,
                contractVersion = RunnerContractVersion.Current,
                sequence,

                // Derived from the sequence rather than random, so a resend of the same batch after a
                // timeout is recognised as the same batch instead of being counted twice.
                idempotencyKey = $"{jobId:N}-{sequence}",
                events,
                fileResults = files,
                spend,
            };

            try
            {
                using var response = await http.PostAsJsonAsync("ingest", request, ct);
                if (response.IsSuccessStatusCode)
                {
                    this._acknowledgedSequence = sequence;
                    return true;
                }

                LogFlushRefused(logger, jobId, sequence, (int)response.StatusCode);

                // A 409 is two different answers wearing one status code. Out-of-order names the batch to
                // resume from and is worth obeying; a lease refusal means the job is not ours any more, and
                // resending is a loop whose answer will not change.
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    var conflict = await ReadConflictAsync(response, ct);
                    if (conflict.LeaseLost)
                    {
                        this._refusedForGood = true;
                    }
                    else if (conflict.ExpectedSequence is { } expected)
                    {
                        // Resume where the ledger says it stopped, which may be behind or ahead of what
                        // this spool believes it sent.
                        this._acknowledgedSequence = expected - 1;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                LogFlushFailed(logger, jobId, sequence, ex.Message);
            }

            // Put it back, at the front, so ordering survives the retry, and under the same sequence: the
            // ledger refuses a gap, so a batch that failed in transit has to be resent as the batch it was.
            Requeue(this._events, events);
            Requeue(this._fileResults, files);
            Requeue(this._spend, spend);
            this.TrimToCeiling();
            return false;
        }
        finally
        {
            this._flushGate.Release();
        }
    }

    /// <summary>
    ///     Reads what a 409 actually said. The ingest conflict carries an expected sequence; a lease refusal
    ///     carries a contract error code instead, and the difference decides whether resending is sensible.
    /// </summary>
    private static async Task<ConflictAnswer> ReadConflictAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;

            if (root.TryGetProperty("code", out var code)
                && code.GetString() is RunnerContractError.LeaseNotHeld or RunnerContractError.RegistrationRevoked)
            {
                return new ConflictAnswer(true, null);
            }

            return root.TryGetProperty("expectedSequence", out var expected)
                   && expected.TryGetInt32(out var value)
                ? new ConflictAnswer(false, value)
                : new ConflictAnswer(false, null);
        }
        catch (JsonException)
        {
            // An answer this client cannot read is not an answer to act on; the batch stays buffered and
            // the next attempt asks again.
            return new ConflictAnswer(false, null);
        }
    }

    /// <summary>
    ///     Drops the oldest trace events once the buffer is past its ceiling, and counts what went. File
    ///     results and spend are never dropped.
    /// </summary>
    private void TrimToCeiling()
    {
        var total = this._events.Count + this._fileResults.Count + this._spend.Count;
        while (total > MaxBufferedItems && this._events.TryDequeue(out _))
        {
            this._droppedEvents++;
            total--;
        }
    }

    private static List<T> Drain<T>(ConcurrentQueue<T> queue)
    {
        var drained = new List<T>(queue.Count);
        while (queue.TryDequeue(out var item))
        {
            drained.Add(item);
        }

        return drained;
    }

    private static void Requeue<T>(ConcurrentQueue<T> queue, List<T> items)
    {
        var later = Drain(queue);
        foreach (var item in items.Concat(later))
        {
            queue.Enqueue(item);
        }
    }

    private readonly record struct ConflictAnswer(bool LeaseLost, int? ExpectedSequence);

    [LoggerMessage(EventId = 6301, Level = LogLevel.Warning, Message = "Batch {Sequence} for job {JobId} was refused with {StatusCode}; it stays buffered")]
    private static partial void LogFlushRefused(ILogger logger, Guid jobId, int sequence, int statusCode);

    [LoggerMessage(EventId = 6302, Level = LogLevel.Warning, Message = "Batch {Sequence} for job {JobId} could not be shipped and stays buffered: {Reason}")]
    private static partial void LogFlushFailed(ILogger logger, Guid jobId, int sequence, string reason);
}
