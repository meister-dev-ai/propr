// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     What a job has already had reviewed, for an executor picking it up again.
///     <para>
///         An in-process execution reads this straight off the job: the file results are rows against the
///         same job id, so a reclaimed job resumes where it stopped and synthesizes over everything, not
///         just what the latest attempt produced. An executor with no database cannot do that, and without
///         it a reclaimed remote review re-pays for every file <em>and</em> publishes only the second half
///         of its own findings.
///     </para>
///     <para>
///         Read on demand rather than carried in the manifest. A completed file result holds its findings,
///         so a job most of the way through a large review would put all of them in every lease offer,
///         while only a reclaim ever needs them.
///     </para>
/// </summary>
public interface IRunnerPriorResultsReader
{
    /// <summary>
    ///     Returns what this job already has recorded, or a refusal when the caller does not hold the lease.
    /// </summary>
    /// <param name="call">The job and generation the caller presents.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerToolResult<IReadOnlyList<RunnerPriorFileResult>>> ReadAsync(
        RunnerCallContext call,
        CancellationToken ct = default);
}

/// <summary>
///     One file this job has already had reviewed, reduced to what the pipeline reads back.
/// </summary>
/// <param name="FilePath">The file.</param>
/// <param name="IsComplete">Whether it finished.</param>
/// <param name="IsFailed">Whether it failed terminally.</param>
/// <param name="IsExcluded">Whether it was excluded rather than reviewed.</param>
/// <param name="ExclusionReason">Why it was excluded.</param>
/// <param name="ErrorMessage">Why it failed.</param>
/// <param name="PerFileSummary">The per-file summary synthesis reads.</param>
/// <param name="ReviewedPassKeys">
///     Which passes have run against it. The selector compares these against the configured pass list, so
///     a file reviewed under an older list is reviewed again rather than kept.
/// </param>
/// <param name="Comments">
///     The findings this file produced. Carried because synthesis reasons over every file's findings, and a
///     resumed review without them publishes only what its latest attempt happened to see.
/// </param>
public sealed record RunnerPriorFileResult(
    string FilePath,
    bool IsComplete,
    bool IsFailed,
    bool IsExcluded,
    string? ExclusionReason,
    string? ErrorMessage,
    string? PerFileSummary,
    IReadOnlyList<string> ReviewedPassKeys,
    IReadOnlyList<ReviewComment> Comments,
    bool IsCarriedForward = false);
