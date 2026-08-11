// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     What findings intake has to remember between requests: the chunks of a submission still being
///     assembled, and which submission each job already published.
///     <para>
///         The intake service is scoped to one HTTP request, and the two things it correlates never arrive
///         on one: a multi-chunk submission is several requests by definition, and the resend the
///         publish-once guard exists for is a second request by definition. State held on the scoped
///         service made every multi-chunk submission assemble forever and every guard test an empty
///         dictionary.
///     </para>
/// </summary>
public sealed class RunnerSubmissionLedger
{
    /// <summary>The submissions still being assembled, one per job.</summary>
    internal ConcurrentDictionary<Guid, RunnerSubmissionAssembly> Assembling { get; } = new();

    /// <summary>Which submission each job published, so a resend is answered rather than posted again.</summary>
    internal ConcurrentDictionary<Guid, string> Published { get; } = new();

    /// <summary>
    ///     Drops what is held for a job once nothing can legitimately resend for it. Its lease has ended on
    ///     this replica, and every later call is refused by lease authorization before it reaches here.
    /// </summary>
    public void Release(Guid jobId)
    {
        this.Assembling.TryRemove(jobId, out _);
        this.Published.TryRemove(jobId, out _);
    }
}

/// <summary>One submission's chunks, in whatever order and on whatever requests they arrive.</summary>
internal sealed class RunnerSubmissionAssembly(string submissionId, int chunkCount)
{
    private readonly Dictionary<int, RunnerFindingsChunk> _chunks = [];
    private readonly Lock _gate = new();

    public bool IsComplete
    {
        get
        {
            lock (this._gate)
            {
                return this._chunks.Count == chunkCount;
            }
        }
    }

    public int Missing
    {
        get
        {
            lock (this._gate)
            {
                return chunkCount - this._chunks.Count;
            }
        }
    }

    public bool Accepts(RunnerFindingsChunk chunk)
    {
        return string.Equals(chunk.SubmissionId, submissionId, StringComparison.Ordinal)
               && chunk.ChunkCount == chunkCount;
    }

    public void Add(RunnerFindingsChunk chunk)
    {
        lock (this._gate)
        {
            this._chunks[chunk.ChunkIndex] = chunk;
        }
    }

    public ReviewResult Build()
    {
        lock (this._gate)
        {
            var ordered = this._chunks.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
            var comments = ordered.SelectMany(chunk => chunk.Comments).ToList();
            var summary = ordered.Select(chunk => chunk.Summary).LastOrDefault(s => !string.IsNullOrEmpty(s));
            var result = new ReviewResult(summary ?? string.Empty, comments);

            // The review's own annotations are carried on the final chunk. They are rebuilt onto the result
            // here so publication reads the same labels an in-process review carries: a soft-capped or
            // context-degraded remote review must not read as complete because it was relayed.
            var annotations = ordered.Select(chunk => chunk.Annotations).LastOrDefault(a => a is not null);
            return annotations is null
                ? result
                : result with
                {
                    CarriedForwardFilePaths = annotations.CarriedForwardFilePaths,
                    CarriedForwardCandidatesSkipped = annotations.CarriedForwardCandidatesSkipped,
                    ContextDegradedFilePaths = annotations.ContextDegradedFilePaths,
                    ContextSkippedFilePaths = annotations.ContextSkippedFilePaths,
                    BudgetSoftCapped = annotations.BudgetSoftCapped,
                    BudgetSoftCapThresholdUsd = annotations.BudgetSoftCapThresholdUsd,
                    BudgetSoftCapSpentUsd = annotations.BudgetSoftCapSpentUsd,
                    BudgetSoftCapSkippedFilePaths = annotations.BudgetSoftCapSkippedFilePaths,
                };
        }
    }
}
