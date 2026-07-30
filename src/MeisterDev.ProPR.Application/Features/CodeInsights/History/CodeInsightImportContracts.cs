// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.History;

/// <summary>
///     What to replay into the collection, for one client.
/// </summary>
/// <remarks>
///     Windowed and bounded because an import reads review results, which are the largest rows the product holds.
///     A run is expected to be repeated: it advances by skipping what has already been collected, so a large
///     window is drained by running it again rather than by raising the bound.
/// </remarks>
/// <param name="ClientId">The client to import. One at a time, so a run's cost belongs to somebody.</param>
/// <param name="From">Inclusive start of the window, by review submission date.</param>
/// <param name="To">Inclusive end of the window, by review submission date.</param>
/// <param name="IncludeOutcomes">
///     Whether to replay what became of each finding and the human threads it missed. This is the only part of an
///     import that calls a model, so it is off unless asked for: findings, roll-ups and coverage cost nothing,
///     while judging months of resolved threads costs real tokens.
/// </param>
/// <param name="MaxJobs">Upper bound on review jobs read in one run.</param>
public sealed record CodeInsightImportRequest(
    Guid ClientId,
    DateOnly From,
    DateOnly To,
    bool IncludeOutcomes = false,
    int MaxJobs = CodeInsightImportRequest.DefaultMaxJobs)
{
    /// <summary>Jobs per run when the caller does not say. Sized so one run stays a page-sized wait.</summary>
    public const int DefaultMaxJobs = 100;

    /// <summary>The ceiling a caller may ask for, so a request cannot turn into an unbounded read.</summary>
    public const int MaxJobsCeiling = 500;
}

/// <summary>
///     What one import run read and wrote.
/// </summary>
/// <remarks>
///     Every number an operator needs to decide whether to run it again, and to tell "nothing left to do" apart
///     from "nothing happened". The two skip counts carry that distinction: work already done, against work this
///     installation can never do.
/// </remarks>
/// <param name="JobsRead">Completed review jobs the run examined.</param>
/// <param name="JobsImported">Jobs whose findings were materialised by this run.</param>
/// <param name="JobsAlreadyCollected">
///     Jobs skipped because the collection already holds findings for them. Skipped rather than merged: a job's
///     findings are identified by their position in it, and a live capture and a replay do not have to agree on
///     that order, so replaying over a job that was already captured could double its findings.
/// </param>
/// <param name="FindingsImported">Findings materialised.</param>
/// <param name="FindingsWithoutThread">
///     Findings imported with no provider thread attached, which no outcome can ever be recorded against. Their
///     posted comments were never linked to a thread on this installation, because provenance was only recorded
///     where thread retention was on. They still count for volume, type and hotspot readings.
/// </param>
/// <param name="PullRequests">Distinct pull requests touched.</param>
/// <param name="OutcomeThreadsReplayed">
///     Resolved ProPR threads handed to the outcome path, always zero unless outcomes were asked for. Threads
///     handed over rather than outcomes written: the outcome path judges each thread and refuses to revise one it
///     has already decided, and overstating that as rows written would be the kind of number this feature exists
///     to stop producing.
/// </param>
/// <param name="HumanThreadsReplayed">
///     Threads handed to the miss harvester, always zero unless outcomes were asked for. It decides for itself
///     which of them count as something the reviewer should have caught.
/// </param>
/// <param name="CollectionDisabled">
///     True when the run did nothing because the licence or the client's opt-in is off. Reported rather than
///     thrown: a closed gate is a setting, not a failure.
/// </param>
/// <param name="ReachedLimit">
///     True when jobs were left unread beyond this run's bound, observed by reading one job past it rather than
///     inferred from having filled the quota. A window holding exactly the bound reports false.
/// </param>
/// <param name="FindingsAlreadyHeld">
///     Findings the collection already held for the jobs in this window. Reported so what was imported plus what
///     was already there can be compared against the findings coverage says those reviews produced: a job the
///     collection holds only part of cannot be repaired by importing over it, because identity is a finding's
///     position, and a gap between these numbers is the only way to see that.
/// </param>
/// <param name="ThreadsNotReplayable">
///     Threads outcomes were asked for but could not be replayed against, because their provider thread id is not
///     the numeric kind the outcome path keys on. An explained zero rather than an unexplained one.
/// </param>
public sealed record CodeInsightImportResult(
    int JobsRead,
    int JobsImported,
    int JobsAlreadyCollected,
    int FindingsImported,
    int FindingsWithoutThread,
    int PullRequests,
    int OutcomeThreadsReplayed,
    int HumanThreadsReplayed,
    bool CollectionDisabled,
    bool ReachedLimit = false,
    int FindingsAlreadyHeld = 0,
    int ThreadsNotReplayable = 0)
{
    /// <summary>The result of a run the gate refused.</summary>
    public static CodeInsightImportResult Gated { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, true);
}
