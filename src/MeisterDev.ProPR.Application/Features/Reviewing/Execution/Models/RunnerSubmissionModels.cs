// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     One part of a submitted review result. A large review's findings do not fit comfortably in one
///     request, and splitting them has to be explicit so a partial arrival is recognisable as partial
///     rather than mistaken for a short review.
/// </summary>
/// <param name="SubmissionId">Identifies the whole submission across its chunks and across retries.</param>
/// <param name="ChunkIndex">Zero-based position of this chunk.</param>
/// <param name="ChunkCount">How many chunks the submission has in total.</param>
/// <param name="Summary">The review summary, carried on the final chunk.</param>
/// <param name="Comments">The findings in this chunk.</param>
/// <param name="Annotations">What the review says about itself, carried on the final chunk.</param>
public sealed record RunnerFindingsChunk(
    string SubmissionId,
    int ChunkIndex,
    int ChunkCount,
    string? Summary,
    IReadOnlyList<ReviewComment> Comments,
    RunnerResultAnnotations? Annotations = null);

/// <summary>
///     Everything a review result says about itself beyond its summary and findings: what was carried
///     forward, what was degraded or skipped for context, and whether the budget cut the scan short.
///     <para>
///         A submission that carried only summary and comments flattened all of it — most visibly the
///         budget label, which made a soft-capped remote review indistinguishable from a complete one
///         everywhere the label is read.
///     </para>
/// </summary>
/// <param name="CarriedForwardFilePaths">Files whose results came from a prior iteration's review.</param>
/// <param name="CarriedForwardCandidatesSkipped">Candidates suppressed because they came from carried-forward results.</param>
/// <param name="ContextDegradedFilePaths">Files reviewed diff-only because their context exceeded the window.</param>
/// <param name="ContextSkippedFilePaths">Files skipped because even their minimal payload exceeded the window.</param>
/// <param name="BudgetSoftCapped">Whether the scan stopped early because the budget soft cap was reached.</param>
/// <param name="BudgetSoftCapThresholdUsd">The cap that was reached, when the executor knew the figure.</param>
/// <param name="BudgetSoftCapSpentUsd">The spend that reached it, when the executor knew the figure.</param>
/// <param name="BudgetSoftCapSkippedFilePaths">Files not scanned because the cap was reached first.</param>
public sealed record RunnerResultAnnotations(
    IReadOnlyList<string> CarriedForwardFilePaths,
    int CarriedForwardCandidatesSkipped,
    IReadOnlyList<string> ContextDegradedFilePaths,
    IReadOnlyList<string> ContextSkippedFilePaths,
    bool BudgetSoftCapped,
    decimal? BudgetSoftCapThresholdUsd,
    decimal? BudgetSoftCapSpentUsd,
    IReadOnlyList<string> BudgetSoftCapSkippedFilePaths);

/// <summary>What happened to a submitted chunk.</summary>
public enum RunnerSubmissionOutcome
{
    /// <summary>The submission is complete and has been handed to publication.</summary>
    Published = 0,

    /// <summary>The chunk was taken; more are needed before anything is published.</summary>
    AwaitingChunks = 1,

    /// <summary>This job already published this submission. Nothing was posted a second time.</summary>
    AlreadyPublished = 2,

    /// <summary>The caller may not act on this job.</summary>
    NotAuthorized = 3,

    /// <summary>The chunk does not fit the submission it claims to belong to.</summary>
    Rejected = 4,
}

/// <summary>The answer to a submitted chunk.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="MissingChunks">How many chunks are still outstanding, when more are awaited.</param>
/// <param name="CallRefusal">Which authorization reason applied, when the chunk was not authorized.</param>
/// <param name="Reason">Why the chunk was rejected, when it was.</param>
public sealed record RunnerSubmissionResult(
    RunnerSubmissionOutcome Outcome,
    int MissingChunks = 0,
    RunnerCallRefusal CallRefusal = RunnerCallRefusal.None,
    string? Reason = null)
{
    /// <summary>Whether the executor's work is done and it may release the job.</summary>
    public bool IsFinished =>
        this.Outcome is RunnerSubmissionOutcome.Published or RunnerSubmissionOutcome.AlreadyPublished;

    /// <summary>The submission was complete and went to publication.</summary>
    public static RunnerSubmissionResult Published()
    {
        return new RunnerSubmissionResult(RunnerSubmissionOutcome.Published);
    }

    /// <summary>The chunk was taken and more are needed.</summary>
    public static RunnerSubmissionResult AwaitingChunks(int missing)
    {
        return new RunnerSubmissionResult(RunnerSubmissionOutcome.AwaitingChunks, missing);
    }

    /// <summary>This submission has already published; nothing was posted again.</summary>
    public static RunnerSubmissionResult AlreadyPublished()
    {
        return new RunnerSubmissionResult(RunnerSubmissionOutcome.AlreadyPublished);
    }

    /// <summary>The caller may not act on this job.</summary>
    public static RunnerSubmissionResult NotAuthorized(RunnerCallRefusal refusal)
    {
        return new RunnerSubmissionResult(RunnerSubmissionOutcome.NotAuthorized, 0, refusal);
    }

    /// <summary>The chunk does not fit what this job is assembling.</summary>
    public static RunnerSubmissionResult Rejected(string reason)
    {
        return new RunnerSubmissionResult(RunnerSubmissionOutcome.Rejected, 0, RunnerCallRefusal.None, reason);
    }
}
