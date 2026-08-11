// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     One batch of output from an executing review: the trace it produced, the files it finished, and what
///     it spent.
/// </summary>
/// <param name="Sequence">
///     Position in this job's stream, starting at 1. Idempotency alone does not give ordering, and a trace
///     with a gap in it cannot be read.
/// </param>
/// <param name="IdempotencyKey">Identifies the batch, so a resend is recognised rather than applied twice.</param>
/// <param name="Events">Trace events, in the order they occurred.</param>
/// <param name="FileResults">Per-file outcomes finished since the last batch.</param>
/// <param name="Spend">Spend records for completions the executor made through the relay.</param>
public sealed record RunnerIngestBatch(
    int Sequence,
    string IdempotencyKey,
    IReadOnlyList<RunnerTraceEvent> Events,
    IReadOnlyList<RunnerFileOutcome> FileResults,
    IReadOnlyList<RunnerSpendRecord> Spend)
{
    /// <summary>How many individual items the batch carries.</summary>
    public int ItemCount => this.Events.Count + this.FileResults.Count + this.Spend.Count;
}

/// <summary>One trace event produced by a remote execution.</summary>
/// <param name="OccurredAt">When it happened, on the executor's clock.</param>
/// <param name="Name">The event name, matching the names the in-process path records.</param>
/// <param name="Details">Structured detail, already serialised.</param>
public sealed record RunnerTraceEvent(DateTimeOffset OccurredAt, string Name, string? Details);

/// <summary>The outcome of reviewing one file, as the resume checkpoint it becomes.</summary>
/// <param name="FilePath">The file reviewed.</param>
/// <param name="IsComplete">Whether the file finished.</param>
/// <param name="IsFailed">Whether it failed.</param>
/// <param name="Summary">The per-file summary, when it completed.</param>
/// <param name="ErrorMessage">The failure, when it failed.</param>
/// <param name="ReviewedPassKeys">Which configured passes produced it, so a later resume can judge it.</param>
/// <param name="Comments">
///     The findings the file produced. These are the checkpoint's substance: a resumed attempt synthesizes
///     over what the rows carry, and rows persisted without their comments hand the next attempt forty
///     finished files that appear to have found nothing.
/// </param>
/// <param name="IsExcluded">Whether the file matched an exclusion and was never reviewed.</param>
/// <param name="ExclusionReason">The matching rule, when it was excluded.</param>
public sealed record RunnerFileOutcome(
    string FilePath,
    bool IsComplete,
    bool IsFailed,
    string? Summary,
    string? ErrorMessage,
    IReadOnlyList<string> ReviewedPassKeys,
    IReadOnlyList<ReviewComment>? Comments = null,
    bool IsExcluded = false,
    string? ExclusionReason = null);

/// <summary>What one relayed completion cost.</summary>
/// <param name="LogicalModelName">The model role that served it.</param>
/// <param name="InputTokens">Input tokens consumed.</param>
/// <param name="OutputTokens">Output tokens produced.</param>
/// <param name="EstimatedCostUsd">Estimated cost, or null when the model has no configured pricing.</param>
public sealed record RunnerSpendRecord(
    string LogicalModelName,
    long InputTokens,
    long OutputTokens,
    decimal? EstimatedCostUsd);

/// <summary>What happened to a batch.</summary>
public enum RunnerIngestOutcome
{
    /// <summary>Applied.</summary>
    Applied = 0,

    /// <summary>Already applied, so nothing was written and nothing was counted again.</summary>
    AlreadyApplied = 1,

    /// <summary>The caller may not act on this job.</summary>
    NotAuthorized = 2,

    /// <summary>The batch exceeded a payload or item ceiling and must be split.</summary>
    TooLarge = 3,

    /// <summary>
    ///     A batch is missing between the last one applied and this one. Applying this would leave a hole in
    ///     the job's trace, so the executor is told which batch to resend from instead.
    /// </summary>
    OutOfOrder = 4,
}

/// <summary>The answer to a batch.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ExpectedSequence">
///     The batch the control plane is waiting for. The executor resends from here, which is the whole
///     backpressure contract: it is told exactly what to do rather than left to guess.
/// </param>
/// <param name="CallRefusal">Which authorization reason applied, when the batch was not authorized.</param>
public sealed record RunnerIngestResult(
    RunnerIngestOutcome Outcome,
    int ExpectedSequence,
    RunnerCallRefusal CallRefusal = RunnerCallRefusal.None)
{
    /// <summary>Whether the executor may drop this batch from its spool.</summary>
    public bool MaySpoolBeTrimmed =>
        this.Outcome is RunnerIngestOutcome.Applied or RunnerIngestOutcome.AlreadyApplied;
}
