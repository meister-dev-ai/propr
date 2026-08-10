// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Takes an executor's batched output and applies it through the same persistence the in-process path
///     uses, exactly once.
/// </summary>
public interface IRunnerIngestService
{
    /// <summary>Applies one batch, or explains why it was not applied.</summary>
    Task<RunnerIngestResult> IngestAsync(
        RunnerCallContext call,
        RunnerIngestBatch batch,
        CancellationToken ct = default);
}

/// <summary>
///     Remembers which batches have been applied for a job, and which one is expected next.
/// </summary>
public interface IRunnerIngestLedger
{
    /// <summary>The sequence the job is waiting for. 1 when nothing has been applied yet.</summary>
    Task<int> GetExpectedSequenceAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    ///     Records a batch as applied. Returns false when this key or sequence was already recorded, which
    ///     is how a resend is recognised without the caller having to check first and race.
    /// </summary>
    Task<bool> TryRecordAsync(Guid jobId, int sequence, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Drops a job's receipts once it is terminal and nothing more can arrive for it.</summary>
    Task ClearAsync(Guid jobId, CancellationToken ct = default);
}

/// <summary>
///     Writes an applied batch through the persistence the in-process path already uses. Separate from the
///     ingest service so the rules about ordering, replay, and ceilings are testable without a database.
/// </summary>
public interface IRunnerIngestWriter
{
    /// <summary>Appends trace events to the job's protocol.</summary>
    Task WriteEventsAsync(Guid jobId, IReadOnlyList<RunnerTraceEvent> events, CancellationToken ct = default);

    /// <summary>Persists per-file outcomes, which become the job's resume checkpoints.</summary>
    Task WriteFileResultsAsync(Guid jobId, IReadOnlyList<RunnerFileOutcome> results, CancellationToken ct = default);

    /// <summary>Records spend against the job's token accounting.</summary>
    Task WriteSpendAsync(Guid jobId, IReadOnlyList<RunnerSpendRecord> spend, CancellationToken ct = default);
}
