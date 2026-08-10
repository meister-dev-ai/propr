// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>
///     Reads what the fleet is carrying, by joining reviews to the runner holding their lease.
/// </summary>
public sealed class RunnerWorkloadReader(MeisterProPRDbContext dbContext) : IRunnerWorkloadReader
{
    /// <summary>
    ///     How many in-flight reviews a single runner row names before it stops naming them. A runner with
    ///     more than this is a runner whose count is the interesting number, not its list.
    /// </summary>
    private const int MaxNamedPerRunner = 10;

    /// <inheritdoc />
    public async Task<RunnerFleetWorkload> GetWorkloadAsync(
        Guid tenantId,
        DateTimeOffset completedSince,
        CancellationToken ct = default)
    {
        var runnerIds = await dbContext.ReviewRunners
            .AsNoTracking()
            .Where(runner => runner.TenantId == tenantId)
            .Select(runner => runner.Id)
            .ToListAsync(ct);

        if (runnerIds.Count == 0)
        {
            return RunnerFleetWorkload.Empty;
        }

        // The lease records its owner as the runner's identifier in text, so the comparison is made in that
        // form rather than parsing every row back into a Guid to compare.
        var owners = runnerIds.ToDictionary(id => id.ToString("D"), id => id, StringComparer.OrdinalIgnoreCase);

        var executing = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(job => job.Status == JobStatus.Processing
                          && job.LeaseOwner != null
                          && owners.Keys.Contains(job.LeaseOwner))
            .Select(job => new
            {
                job.Id,
                job.LeaseOwner,
                job.PrRepositoryName,
                job.PullRequestId,
                job.PrTitle,
                job.ProcessingStartedAt,
                job.TotalReclaimCount,
            })
            .ToListAsync(ct);

        var completed = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(job => job.Status == JobStatus.Completed
                          && job.CompletedAt >= completedSince
                          && job.LeaseOwner != null
                          && owners.Keys.Contains(job.LeaseOwner))
            .GroupBy(job => job.LeaseOwner!)
            .Select(group => new { Owner = group.Key, Count = group.Count() })
            .ToListAsync(ct);

        // Reviews waiting for this tenant's fleet, which is not the same as reviews waiting anywhere: a
        // queue backed up in another tenant says nothing about whether these runners are keeping up.
        var tenantClients = dbContext.Clients
            .AsNoTracking()
            .Where(client => client.TenantId == tenantId)
            .Select(client => client.Id);

        var pending = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(job => job.Status == JobStatus.Pending && tenantClients.Contains(job.ClientId))
            .Select(job => job.SubmittedAt)
            .ToListAsync(ct);

        var completedByOwner = completed.ToDictionary(
            entry => entry.Owner,
            entry => entry.Count,
            StringComparer.OrdinalIgnoreCase);

        var byRunner = new Dictionary<Guid, RunnerWorkload>();

        foreach (var group in executing.GroupBy(job => job.LeaseOwner!, StringComparer.OrdinalIgnoreCase))
        {
            if (!owners.TryGetValue(group.Key, out var runnerId))
            {
                continue;
            }

            byRunner[runnerId] = new RunnerWorkload(
                group.Count(),
                completedByOwner.GetValueOrDefault(group.Key),
                [
                    .. group
                        .OrderBy(job => job.ProcessingStartedAt)
                        .Take(MaxNamedPerRunner)
                        .Select(job => new RunnerExecutingJob(
                            job.Id,
                            job.PrRepositoryName,
                            job.PullRequestId,
                            job.PrTitle,
                            job.ProcessingStartedAt,
                            job.TotalReclaimCount)),
                ]);
        }

        // A runner that finished work but holds none right now still belongs in the answer: "idle, and did
        // twelve reviews today" and "idle, and has never done anything" are different states, and only one
        // of them is a problem.
        foreach (var (owner, count) in completedByOwner)
        {
            if (owners.TryGetValue(owner, out var runnerId) && !byRunner.ContainsKey(runnerId))
            {
                byRunner[runnerId] = new RunnerWorkload(0, count, []);
            }
        }

        return new RunnerFleetWorkload(
            byRunner,
            pending.Count,
            pending.Count == 0 ? null : pending.Min());
    }
}
