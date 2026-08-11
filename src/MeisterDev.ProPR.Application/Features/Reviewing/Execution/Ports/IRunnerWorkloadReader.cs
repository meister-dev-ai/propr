// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     What the fleet is doing right now, as opposed to what it is.
///     <para>
///         The registry answers which runners exist and whether they are healthy. It cannot answer the
///         two questions an operator arrives with, whether the work is progressing and which host holds it,
///         because those live on the jobs rather than the runners. Reading it here keeps the two joined in
///         one place instead of leaving a browser to correlate a list of hosts with a list of jobs.
///     </para>
/// </summary>
public interface IRunnerWorkloadReader
{
    /// <summary>What every runner in a tenant currently holds, and what is waiting for one.</summary>
    /// <param name="tenantId">The tenant whose fleet to read.</param>
    /// <param name="completedSince">How far back to count finished reviews, for the throughput figure.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerFleetWorkload> GetWorkloadAsync(
        Guid tenantId,
        DateTimeOffset completedSince,
        CancellationToken ct = default);
}

/// <summary>The fleet's current work, per runner and in total.</summary>
/// <param name="ByRunner">Workload keyed by runner. A runner holding nothing is absent rather than zeroed.</param>
/// <param name="PendingJobCount">Reviews waiting for any runner in this tenant.</param>
/// <param name="OldestPendingSince">When the longest-waiting review was submitted, or null when none wait.</param>
public sealed record RunnerFleetWorkload(
    IReadOnlyDictionary<Guid, RunnerWorkload> ByRunner,
    int PendingJobCount,
    DateTimeOffset? OldestPendingSince)
{
    /// <summary>An empty fleet, for installations with no runners and for a reader that cannot answer.</summary>
    public static RunnerFleetWorkload Empty { get; } =
        new(new Dictionary<Guid, RunnerWorkload>(), 0, null);

    /// <summary>Reviews executing across the whole fleet.</summary>
    public int ExecutingJobCount => this.ByRunner.Values.Sum(workload => workload.ExecutingCount);
}

/// <summary>What one runner holds and what it has finished.</summary>
/// <param name="ExecutingCount">Reviews it holds a lease on right now.</param>
/// <param name="CompletedCount">Reviews it finished within the window asked for.</param>
/// <param name="Executing">The reviews themselves, so the row names work rather than counting it.</param>
public sealed record RunnerWorkload(
    int ExecutingCount,
    int CompletedCount,
    IReadOnlyList<RunnerExecutingJob> Executing);

/// <summary>One review a runner is holding.</summary>
/// <param name="JobId">The review job.</param>
/// <param name="RepositoryName">The repository, as the provider names it.</param>
/// <param name="PullRequestNumber">The pull request under review.</param>
/// <param name="Title">The pull request's title, when the job recorded one.</param>
/// <param name="StartedAt">When the runner started it.</param>
/// <param name="ReclaimCount">
///     How many times this review has been taken back from a runner that stopped reporting. A non-zero
///     value on work in flight is the difference between a slow review and one that keeps being retried.
/// </param>
public sealed record RunnerExecutingJob(
    Guid JobId,
    string? RepositoryName,
    int PullRequestNumber,
    string? Title,
    DateTimeOffset? StartedAt,
    int ReclaimCount);
