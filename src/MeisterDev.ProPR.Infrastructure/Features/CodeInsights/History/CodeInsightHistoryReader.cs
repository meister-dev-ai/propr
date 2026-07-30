// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.History;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.History;

/// <summary>
///     Counts what review history holds against what the collection holds, per repository.
/// </summary>
/// <remarks>
///     <para>
///         Every count is a row count or a sum computed in the database. The findings a job produced live in a
///         <c>jsonb</c> array on its file results, so they are counted with <c>jsonb_array_length</c> rather than
///         by loading the arrays: a window of review results is tens of megabytes of text, and this read has to
///         be cheap enough to sit on a page.
///     </para>
///     <para>
///         The window is by review submission date, because that is what an import would select on, and it is
///         the date the collection buckets a finding under.
///     </para>
///     <para>
///         Findings produced are counted once per reviewed revision rather than once per job. A revision reviewed
///         twice persists two jobs' worth of findings in review history, while the collection holds one set for it:
///         identity is the revision and the finding's position in it, so the second review's findings land on the
///         first review's rows. Summing per job would report such a repository as permanently half collected. The
///         largest job in a revision is what the collection can hold for it, which is what the collected count is
///         then compared against.
///     </para>
/// </remarks>
public sealed class CodeInsightHistoryReader(
    MeisterProPRDbContext dbContext,
    ICodeInsightsCollectionGate gate,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : ICodeInsightHistoryReader
{
    /// <inheritdoc />
    public Task<CodeInsightHistoryCoverage> GetCoverageAsync(
        CodeInsightHistoryCoverageQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return this.WithDbAsync(
            async db =>
            {
                if (query.ClientIds.Count == 0)
                {
                    return CodeInsightHistoryCoverage.Empty;
                }

                var clientIds = query.ClientIds.Distinct().ToList();
                var from = query.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                // Inclusive end: the window is expressed in whole days.
                var to = query.To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

                var projections = await db.ReviewJobs
                    .AsNoTracking()
                    .Where(job => clientIds.Contains(job.ClientId)
                                  && job.Status == JobStatus.Completed
                                  && job.SubmittedAt >= from
                                  && job.SubmittedAt < to)
                    .Select(job => new JobProjection(
                        job.Id,
                        job.ClientId,
                        job.RepositoryId,
                        (long)job.PullRequestId,
                        job.IterationId,
                        job.RevisionHeadSha,
                        job.RevisionBaseSha,
                        job.RevisionStartSha,
                        job.ProviderRevisionId,
                        job.ReviewPatchIdentity))
                    .ToListAsync(ct);

                var jobs = projections.Select(ToJobRow).ToList();

                if (jobs.Count == 0)
                {
                    return CodeInsightHistoryCoverage.Empty;
                }

                var jobIds = jobs.Select(job => job.JobId).ToList();
                var produced = await CountProducedFindingsAsync(db, jobIds, ct);
                var collected = await LoadCollectedAsync(db, jobIds, ct);
                var retained = await LoadRetainedAsync(db, clientIds, jobs, ct);
                var sealedPullRequests = await LoadSealedAsync(db, clientIds, jobs, ct);
                var names = await LoadRepositoryNamesAsync(db, clientIds, jobs, ct);

                var rows = jobs
                    .GroupBy(job => (job.ClientId, job.RepositoryId))
                    .Select(group => BuildRow(group.Key, group, produced, collected, retained, sealedPullRequests, names))
                    // Least covered first: the row a reader has to act on is the one furthest behind.
                    .OrderBy(row => row.ProducedFindings == 0
                        ? 1d
                        : (double)row.CollectedFindings / row.ProducedFindings)
                    .ThenByDescending(row => row.ProducedFindings)
                    .ThenBy(row => row.RepositoryId, StringComparer.Ordinal)
                    .ToList();

                var off = new List<Guid>();
                foreach (var clientId in rows.Select(row => row.ClientId).Distinct())
                {
                    if (!await gate.IsCollectionEnabledAsync(clientId, ct))
                    {
                        off.Add(clientId);
                    }
                }

                return new CodeInsightHistoryCoverage(
                    rows,
                    rows.Sum(row => row.ReviewJobs),
                    rows.Sum(row => row.JobsCollected),
                    rows.Sum(row => row.ProducedFindings),
                    rows.Sum(row => row.CollectedFindings),
                    rows.Sum(row => row.PullRequests),
                    rows.Sum(row => row.PullRequestsRetained),
                    off.Count);
            },
            ct);
    }

    /// <summary>
    ///     Resolves the same revision key the collection stores a finding under, from the same builder, so the two
    ///     sides of this comparison cannot drift apart. The guard mirrors the job's own: without both a head and a
    ///     base commit there is no revision to key on, and the iteration stands in for it.
    /// </summary>
    private static JobRow ToJobRow(JobProjection job)
    {
        var revision = string.IsNullOrWhiteSpace(job.HeadSha) || string.IsNullOrWhiteSpace(job.BaseSha)
            ? null
            : new ReviewRevision(job.HeadSha, job.BaseSha, job.StartSha, job.ProviderRevisionId, job.PatchIdentity);

        return new JobRow(
            job.JobId,
            job.ClientId,
            job.RepositoryId,
            job.PullRequestId,
            ReviewRevisionKeys.GetStoredKey(revision, job.IterationId));
    }

    /// <summary>
    ///     Counts produced findings once per reviewed revision, taking the largest job in each. That is what the
    ///     collection can hold for the revision: a shorter re-review lands entirely on the first review's rows,
    ///     and a longer one adds only the positions the first did not reach.
    /// </summary>
    private static int SumProducedPerRevision(
        IReadOnlyList<JobRow> jobs,
        IReadOnlyDictionary<Guid, int> produced)
    {
        return jobs
            .GroupBy(job => (job.PullRequestId, job.RevisionKey))
            .Sum(revision => revision.Max(job => produced.TryGetValue(job.JobId, out var count) ? count : 0));
    }

    private static CodeInsightHistoryCoverageRow BuildRow(
        (Guid ClientId, string RepositoryId) key,
        IEnumerable<JobRow> group,
        IReadOnlyDictionary<Guid, int> produced,
        IReadOnlyDictionary<Guid, int> collected,
        RetainedCounts retained,
        IReadOnlySet<(Guid ClientId, string RepositoryId, long PullRequestId)> sealedPullRequests,
        IReadOnlyDictionary<(Guid ClientId, string RepositoryId), string> names)
    {
        var jobs = group.ToList();
        var pullRequests = jobs.Select(job => job.PullRequestId).Distinct().ToList();

        return new CodeInsightHistoryCoverageRow(
            key.ClientId,
            null,
            key.RepositoryId,
            names.TryGetValue(key, out var name) ? name : null,
            jobs.Count,
            jobs.Count(job => collected.TryGetValue(job.JobId, out var count) && count > 0),
            SumProducedPerRevision(jobs, produced),
            jobs.Sum(job => collected.TryGetValue(job.JobId, out var count) ? count : 0),
            pullRequests.Count,
            pullRequests.Count(id => retained.PullRequests.Contains((key.ClientId, key.RepositoryId, id))),
            pullRequests.Sum(id =>
                retained.Threads.TryGetValue((key.ClientId, key.RepositoryId, id), out var count) ? count : 0),
            jobs.Sum(job => retained.Dispositions.TryGetValue(job.JobId, out var count) ? count : 0),
            pullRequests.Sum(id =>
                retained.Misses.TryGetValue((key.ClientId, key.RepositoryId, id), out var count) ? count : 0),
            pullRequests.Count(id => sealedPullRequests.Contains((key.ClientId, key.RepositoryId, id))));
    }

    /// <summary>
    ///     Sums the findings each job persisted, in the database. Excluded and failed file results carry no
    ///     comment array and contribute nothing.
    /// </summary>
    private static async Task<IReadOnlyDictionary<Guid, int>> CountProducedFindingsAsync(
        MeisterProPRDbContext db,
        IReadOnlyList<Guid> jobIds,
        CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
        {
            // The in-memory provider used by unit tests has no json functions, so the arrays are counted after
            // loading. Test fixtures are small; production never takes this path.
            var loaded = await db.ReviewFileResults
                .AsNoTracking()
                .Where(result => jobIds.Contains(result.JobId))
                .Select(result => new { result.JobId, Count = result.Comments == null ? 0 : result.Comments.Count })
                .ToListAsync(ct);

            return loaded
                .GroupBy(row => row.JobId)
                .ToDictionary(group => group.Key, group => group.Sum(row => row.Count));
        }

        // A command rather than SqlQuery: that helper projects scalar values, and this needs two columns per row.
        // Nothing here is interpolated into the text; the job ids travel as a uuid[] parameter.
        var sums = new Dictionary<Guid, int>();
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT job_id, COALESCE(SUM(jsonb_array_length(comments_json)), 0)::int AS finding_count
                FROM review_file_results
                WHERE job_id = ANY(@jobIds) AND comments_json IS NOT NULL
                GROUP BY job_id
                """;

            var parameter = command.CreateParameter();
            parameter.ParameterName = "jobIds";
            parameter.Value = jobIds.ToArray();
            command.Parameters.Add(parameter);

            await using var rows = await command.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
            {
                sums[rows.GetGuid(0)] = rows.GetInt32(1);
            }
        }
        finally
        {
            if (openedHere)
            {
                await db.Database.CloseConnectionAsync();
            }
        }

        return sums;
    }

    private static async Task<IReadOnlyDictionary<Guid, int>> LoadCollectedAsync(
        MeisterProPRDbContext db,
        IReadOnlyList<Guid> jobIds,
        CancellationToken ct)
    {
        var rows = await db.CodeInsightFindings
            .AsNoTracking()
            .Where(finding => jobIds.Contains(finding.JobId))
            .GroupBy(finding => finding.JobId)
            .Select(group => new ProducedRow(group.Key, group.Count()))
            .ToListAsync(ct);

        return rows.ToDictionary(row => row.JobId, row => row.Count);
    }

    private static async Task<RetainedCounts> LoadRetainedAsync(
        MeisterProPRDbContext db,
        IReadOnlyList<Guid> clientIds,
        IReadOnlyList<JobRow> jobs,
        CancellationToken ct)
    {
        var pullRequestIds = jobs.Select(job => job.PullRequestId).Distinct().ToList();

        var retained = await db.RetainedPullRequests
            .AsNoTracking()
            .Where(pr => clientIds.Contains(pr.ClientId) && pullRequestIds.Contains(pr.PullRequestId))
            .Select(pr => new
            {
                pr.ClientId,
                pr.RepositoryId,
                pr.PullRequestId,
                Threads = pr.Threads.Count,
            })
            .ToListAsync(ct);

        var dispositions = await db.CodeInsightFindingDispositions
            .AsNoTracking()
            .Where(disposition => jobs.Select(job => job.JobId).Contains(disposition.CodeInsightFinding!.JobId))
            .GroupBy(disposition => disposition.CodeInsightFinding!.JobId)
            .Select(group => new ProducedRow(group.Key, group.Count()))
            .ToListAsync(ct);

        var misses = await db.CodeInsightMisses
            .AsNoTracking()
            .Where(miss => clientIds.Contains(miss.CodeInsightPullRequest!.ClientId)
                           && pullRequestIds.Contains(miss.CodeInsightPullRequest!.PullRequestId))
            .GroupBy(miss => new
            {
                miss.CodeInsightPullRequest!.ClientId,
                miss.CodeInsightPullRequest!.RepositoryId,
                miss.CodeInsightPullRequest!.PullRequestId,
            })
            .Select(group => new
            {
                group.Key.ClientId,
                group.Key.RepositoryId,
                group.Key.PullRequestId,
                Count = group.Count(),
            })
            .ToListAsync(ct);

        return new RetainedCounts(
            retained.Select(pr => (pr.ClientId, pr.RepositoryId, pr.PullRequestId)).ToHashSet(),
            retained.ToDictionary(pr => (pr.ClientId, pr.RepositoryId, pr.PullRequestId), pr => pr.Threads),
            dispositions.ToDictionary(row => row.JobId, row => row.Count),
            misses.ToDictionary(row => (row.ClientId, row.RepositoryId, row.PullRequestId), row => row.Count));
    }

    private static async Task<IReadOnlySet<(Guid ClientId, string RepositoryId, long PullRequestId)>>
        LoadSealedAsync(
            MeisterProPRDbContext db,
            IReadOnlyList<Guid> clientIds,
            IReadOnlyList<JobRow> jobs,
            CancellationToken ct)
    {
        var pullRequestIds = jobs.Select(job => job.PullRequestId).Distinct().ToList();

        var rows = await db.CodeInsightPullRequestMetrics
            .AsNoTracking()
            .Where(metric => clientIds.Contains(metric.ClientId) && pullRequestIds.Contains(metric.PullRequestId))
            .Select(metric => new { metric.ClientId, metric.RepositoryId, metric.PullRequestId })
            .ToListAsync(ct);

        return rows.Select(row => (row.ClientId, row.RepositoryId, row.PullRequestId)).ToHashSet();
    }

    /// <summary>
    ///     A display name for each repository in the window, from the collection where it has one and from the
    ///     reviews themselves where it does not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two sources, because this table is about the repositories the collection does <em>not</em> know.
    ///         The collected name is preferred: it is refreshed on every pull request, so a renamed repository
    ///         settles on its current name. A repository with nothing collected has no such row by definition,
    ///         which is exactly the row this table exists to show, so the name recorded by its reviews is used
    ///         instead.
    ///     </para>
    ///     <para>
    ///         The review lookup asks only about the repositories in the window and takes distinct names, so a
    ///         repository with a thousand reviews contributes one row rather than a thousand. That also removes
    ///         the earlier flat cap, which ordered every client's reviews by date and truncated: a repository
    ///         quiet for a few months fell off the end and lost its name for no reason a reader could see.
    ///         Where a repository was renamed and nothing was ever collected for it, either recorded name is as
    ///         defensible as the other, and both beat printing the provider's identifier.
    ///     </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<(Guid ClientId, string RepositoryId), string>>
        LoadRepositoryNamesAsync(
            MeisterProPRDbContext db,
            IReadOnlyList<Guid> clientIds,
            IReadOnlyList<JobRow> jobs,
            CancellationToken ct)
    {
        var names = new Dictionary<(Guid, string), string>();

        foreach (var collected in await CodeInsightRepositoryNames.LoadAsync(db, clientIds, ct))
        {
            names[collected.Key] = collected.Value;
        }

        var repositoryIds = jobs
            .Where(job => !names.ContainsKey((job.ClientId, job.RepositoryId)))
            .Select(job => job.RepositoryId)
            .Distinct()
            .ToList();

        if (repositoryIds.Count == 0)
        {
            return names;
        }

        var recorded = await db.ReviewJobs
            .AsNoTracking()
            .Where(job => clientIds.Contains(job.ClientId)
                          && repositoryIds.Contains(job.RepositoryId)
                          && job.PrRepositoryName != null
                          && job.PrRepositoryName != string.Empty)
            .Select(job => new { job.ClientId, job.RepositoryId, job.PrRepositoryName })
            .Distinct()
            .ToListAsync(ct);

        foreach (var row in recorded)
        {
            names.TryAdd((row.ClientId, row.RepositoryId), row.PrRepositoryName!);
        }

        return names;
    }

    private async Task<T> WithDbAsync<T>(Func<MeisterProPRDbContext, Task<T>> operation, CancellationToken ct)
    {
        if (contextFactory is null)
        {
            return await operation(dbContext);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await operation(db);
    }

    private readonly record struct JobRow(
        Guid JobId,
        Guid ClientId,
        string RepositoryId,
        long PullRequestId,
        string RevisionKey);

    /// <summary>
    ///     What the database returns per job. The revision key cannot be computed in SQL, so the columns it is
    ///     derived from come back raw and the key is resolved in memory.
    /// </summary>
    private readonly record struct JobProjection(
        Guid JobId,
        Guid ClientId,
        string RepositoryId,
        long PullRequestId,
        int IterationId,
        string? HeadSha,
        string? BaseSha,
        string? StartSha,
        string? ProviderRevisionId,
        string? PatchIdentity);

    private readonly record struct ProducedRow(Guid JobId, int Count);

    private readonly record struct RetainedCounts(
        IReadOnlySet<(Guid ClientId, string RepositoryId, long PullRequestId)> PullRequests,
        IReadOnlyDictionary<(Guid ClientId, string RepositoryId, long PullRequestId), int> Threads,
        IReadOnlyDictionary<Guid, int> Dispositions,
        IReadOnlyDictionary<(Guid ClientId, string RepositoryId, long PullRequestId), int> Misses);
}
