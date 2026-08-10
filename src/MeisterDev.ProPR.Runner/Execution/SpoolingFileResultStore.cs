// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     The pipeline's per-file persistence, on a host with no database.
///     <para>
///         The job lives in memory for the length of the lease, seeded from the manifest, and each file's
///         result is buffered for ingest as it finishes. That buffering is what makes a file the resume
///         checkpoint it is meant to be: the control plane records the outcome as it arrives, so a review
///         interrupted after forty files resumes at forty-one rather than starting again.
///     </para>
///     <para>
///         The job it holds is seeded with whatever the control plane already recorded, read back at the
///         start of the execution. That is what lets a reclaimed job skip what it finished — and, more than
///         cost, what keeps synthesis reasoning over the whole review rather than over the half this
///         attempt happened to produce.
///     </para>
/// </summary>
public sealed class SpoolingFileResultStore(ReviewJob job, JobSpool spool) : IReviewFileResultStore
{
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public Task<ReviewJob?> GetByIdWithFileResultsAsync(Guid id, CancellationToken ct = default)
    {
        // One job per lease, so an id that is not this one is a wiring mistake rather than a miss, and
        // answering null would let the pipeline quietly review the wrong thing.
        if (id != job.Id)
        {
            throw new InvalidOperationException($"This runner holds job {job.Id} and was asked for {id}.");
        }

        return Task.FromResult<ReviewJob?>(job);
    }

    /// <inheritdoc />
    public Task AddFileResultAsync(ReviewFileResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (this._gate)
        {
            job.FileReviewResults.Add(result);
        }

        this.Buffer(result);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateFileResultAsync(ReviewFileResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Buffered again rather than replaced in the batch. The control plane keys file outcomes by path
        // and takes the last one, so a retry's result wins there without this having to reach into a
        // batch that may already have been shipped.
        this.Buffer(result);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateInScopeChangedFileCountAsync(Guid id, int count, CancellationToken ct = default)
    {
        spool.Add(
            "review.in_scope_file_count",
            $"{{\"jobId\":\"{id}\",\"count\":{count}}}",
            DateTimeOffset.UtcNow);

        return Task.CompletedTask;
    }

    private void Buffer(ReviewFileResult result)
    {
        spool.Add(
            new RunnerFileOutcome(
                result.FilePath,
                result.IsComplete,
                result.IsFailed,
                result.PerFileSummary,
                result.ErrorMessage,
                [.. result.ReviewedPassKeys],
                // The findings travel with the checkpoint. A row persisted without them reads, on the next
                // attempt, as a finished file that found nothing — and synthesis believes it.
                result.Comments is { Count: > 0 } ? [.. result.Comments] : null,
                result.IsExcluded,
                result.ExclusionReason));
    }
}
