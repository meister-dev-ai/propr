// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Threads.Persistence;

/// <summary>
///     EF Core implementation of <see cref="IThreadPassJobRepository" />, backed by PostgreSQL.
/// </summary>
/// <remarks>
///     Every claim this repository grants is decided by the database, never by a read the caller then acts on:
///     the in-flight claim by a partial unique index over the pull request, the repeat-trigger claim by a
///     unique index on the trigger state, and the per-thread record by a unique index on the thread, the
///     comment count and the revision. Two crawl configurations over one repository and two deployed instances
///     therefore cannot both act, whatever trigger state each of them arrived with.
///     <para>
///         Granted, rather than answered: a claim may be <em>refused</em> by a read, because a contender the
///         read can already see is one the index would refuse anyway. That shortcut is worth taking only
///         because a refusal cannot be wrong in the direction that matters. A read that finds nothing grants
///         nothing.
///     </para>
/// </remarks>
public sealed class EfThreadPassJobRepository(MeisterProPRDbContext dbContext) : IThreadPassJobRepository
{
    private static readonly ThreadPassJobStatus[] InFlightStatuses =
    [
        ThreadPassJobStatus.Pending,
        ThreadPassJobStatus.Processing,
    ];

    /// <inheritdoc />
    public async Task<TryClaimThreadPassResult> TryClaimAsync(ThreadPassJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        // A contender that is already committed is the ordinary case, not a race: a pass in flight stays in
        // flight for as long as it takes to answer, and every crawl tick in between arrives here. Skipping the
        // insert for one we can already see spares the log an exception per tick, which is the difference
        // between an error meaning something and an error meaning a pass is running.
        //
        // This read decides nothing. It cannot: two writers arriving together both see no contender, and a
        // reply cannot be unposted afterwards. The insert below is still the claim, and the unique indexes are
        // still what settle a genuine race, so the only thing the read changes is how often the loser has to
        // find out by exception.
        var committedContender = await this.FindBlockingPassAsync(job, ct);
        if (committedContender is not null)
        {
            return new TryClaimThreadPassResult(false, committedContender);
        }

        dbContext.ThreadPassJobs.Add(job);

        try
        {
            await dbContext.SaveChangesAsync(ct);
            return new TryClaimThreadPassResult(true, null);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            dbContext.Entry(job).State = EntityState.Detached;
            var blocking = await this.FindBlockingPassAsync(job, ct);
            return new TryClaimThreadPassResult(false, blocking);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadPassJob>> GetPendingAsync(int maxCount, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        return await dbContext.ThreadPassJobs
            .AsNoTracking()
            .Where(job => job.Status == ThreadPassJobStatus.Pending
                          && job.AttemptCount < ThreadPassJob.MaxAttempts
                          && (job.NextAttemptAt == null || job.NextAttemptAt <= now))
            .OrderBy(job => job.CreatedAt)
            .Take(maxCount)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> TryBeginAttemptAsync(Guid jobId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // The retry delay is enforced here as well as in the query that offers the row, because a worker may
        // be holding an offer made before the last attempt failed. Without this an offer made seconds ago
        // spends the next attempt the instant it is consumed.
        var affected = await dbContext.ThreadPassJobs
            .Where(job => job.Id == jobId
                          && job.Status == ThreadPassJobStatus.Pending
                          && job.AttemptCount < ThreadPassJob.MaxAttempts
                          && (job.NextAttemptAt == null || job.NextAttemptAt <= now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ThreadPassJobStatus.Processing)
                    .SetProperty(job => job.AttemptCount, job => job.AttemptCount + 1)
                    .SetProperty(job => job.ProcessingStartedAt, (DateTimeOffset?)DateTimeOffset.UtcNow),
                ct);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<bool> SetCompletedAsync(Guid jobId, CancellationToken ct = default)
    {
        // Conditional on the row still running: a pass cancelled because its pull request closed must not be
        // returned to a terminal success by the attempt that was in flight when the cancellation landed.
        var affected = await dbContext.ThreadPassJobs
            .Where(job => job.Id == jobId && job.Status == ThreadPassJobStatus.Processing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ThreadPassJobStatus.Completed)
                    .SetProperty(job => job.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow)
                    .SetProperty(job => job.ErrorMessage, (string?)null),
                ct);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<bool> SetSkippedAsync(Guid jobId, string reason, CancellationToken ct = default)
    {
        var affected = await dbContext.ThreadPassJobs
            .Where(job => job.Id == jobId && job.Status == ThreadPassJobStatus.Processing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ThreadPassJobStatus.Skipped)
                    .SetProperty(job => job.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow)
                    .SetProperty(job => job.ErrorMessage, reason),
                ct);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task SetCancelledAsync(Guid jobId, CancellationToken ct = default)
    {
        await dbContext.ThreadPassJobs
            .Where(job => job.Id == jobId && InFlightStatuses.Contains(job.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ThreadPassJobStatus.Cancelled)
                    .SetProperty(job => job.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow),
                ct);
    }

    /// <inheritdoc />
    public async Task SetBudgetHeldAsync(
        Guid jobId,
        BudgetScopeKind scope,
        BudgetCapKind capKind,
        decimal thresholdUsd,
        decimal spentUsd,
        CancellationToken ct = default)
    {
        // Held only from the queued state, before anything claimed it: a pass already running has spent money
        // this hold would misreport as never started.
        await dbContext.ThreadPassJobs
            .Where(job => job.Id == jobId && job.Status == ThreadPassJobStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ThreadPassJobStatus.BudgetHeld)
                    .SetProperty(job => job.BudgetBlockScope, (BudgetScopeKind?)scope)
                    .SetProperty(job => job.BudgetBlockCapKind, (BudgetCapKind?)capKind)
                    .SetProperty(job => job.BudgetBlockThresholdUsd, (decimal?)thresholdUsd)
                    .SetProperty(job => job.BudgetBlockSpentUsd, (decimal?)spentUsd),
                ct);
    }

    /// <inheritdoc />
    public async Task SetBudgetExceededAsync(
        Guid jobId,
        BudgetScopeKind scope,
        BudgetCapKind capKind,
        decimal thresholdUsd,
        decimal spentUsd,
        CancellationToken ct = default)
    {
        // Terminal only from the running state, on the same terms as completion: a cancelled pull request has
        // already spoken for the row.
        await dbContext.ThreadPassJobs
            .Where(job => job.Id == jobId && job.Status == ThreadPassJobStatus.Processing)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ThreadPassJobStatus.BudgetExceeded)
                    .SetProperty(job => job.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow)
                    .SetProperty(job => job.BudgetBlockScope, (BudgetScopeKind?)scope)
                    .SetProperty(job => job.BudgetBlockCapKind, (BudgetCapKind?)capKind)
                    .SetProperty(job => job.BudgetBlockThresholdUsd, (decimal?)thresholdUsd)
                    .SetProperty(job => job.BudgetBlockSpentUsd, (decimal?)spentUsd),
                ct);
    }

    /// <inheritdoc />
    public async Task<bool> TryRestartAsync(Guid jobId, CancellationToken ct = default)
    {
        // A review restart clones its source, because a review job row is the record of one attempt. A pass is
        // unique on the trigger state that created it, so a clone would be refused as a duplicate of itself:
        // the same row returning to pending is what a restart means here.
        var restarted = await dbContext.ThreadPassJobs
            .Where(job => job.Id == jobId
                          && (job.Status == ThreadPassJobStatus.BudgetHeld
                              || job.Status == ThreadPassJobStatus.BudgetExceeded
                              || job.Status == ThreadPassJobStatus.Failed))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ThreadPassJobStatus.Pending)
                    .SetProperty(job => job.AttemptCount, 0)
                    .SetProperty(job => job.ProcessingStartedAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.CompletedAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.ErrorMessage, (string?)null)
                    .SetProperty(job => job.BudgetBlockScope, (BudgetScopeKind?)null)
                    .SetProperty(job => job.BudgetBlockCapKind, (BudgetCapKind?)null)
                    .SetProperty(job => job.BudgetBlockThresholdUsd, (decimal?)null)
                    .SetProperty(job => job.BudgetBlockSpentUsd, (decimal?)null),
                ct);

        return restarted == 1;
    }

    /// <inheritdoc />
    public async Task SetAiConfigAsync(
        Guid jobId,
        Guid? connectionId,
        string? model,
        CancellationToken ct = default)
    {
        await dbContext.ThreadPassJobs
            .Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.AiConnectionId, connectionId)
                    .SetProperty(job => job.AiModel, model),
                ct);
    }

    /// <inheritdoc />
    public async Task<ThreadPassJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default)
    {
        return await dbContext.ThreadPassJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(job => job.Id == jobId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadPassJob>> GetForPullRequestAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int maxCount,
        CancellationToken ct = default)
    {
        return await dbContext.ThreadPassJobs
            .AsNoTracking()
            .Include(job => job.HandledThreads)
            .Where(job => job.ClientId == clientId
                          && job.OrganizationUrl == organizationUrl
                          && job.ProjectId == projectId
                          && job.RepositoryId == repositoryId
                          && job.PullRequestId == pullRequestId)
            .OrderByDescending(job => job.CreatedAt)
            .Take(maxCount)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> RecordAttemptFailureAsync(
        Guid jobId,
        string errorMessage,
        CancellationToken ct = default)
    {
        // Decided against the stored attempt count rather than an in-memory copy of it, because the count is
        // spent by a separate statement and any copy taken before that is already behind. Both statements are
        // conditional on the pass still running, so an attempt that finishes after its pull request closed
        // neither fails a cancelled row nor resurrects it to pending.
        var exhausted = await dbContext.ThreadPassJobs
            .Where(job => job.Id == jobId
                          && job.Status == ThreadPassJobStatus.Processing
                          && job.AttemptCount >= ThreadPassJob.MaxAttempts)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ThreadPassJobStatus.Failed)
                    .SetProperty(job => job.ErrorMessage, errorMessage)
                    .SetProperty(job => job.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow),
                ct);
        if (exhausted > 0)
        {
            return false;
        }

        var nextAttemptAt = DateTimeOffset.UtcNow + ThreadPassJob.RetryDelay;
        var returnedToPending = await dbContext.ThreadPassJobs
            .Where(job => job.Id == jobId
                          && job.Status == ThreadPassJobStatus.Processing
                          && job.AttemptCount < ThreadPassJob.MaxAttempts)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ThreadPassJobStatus.Pending)
                    .SetProperty(job => job.ErrorMessage, errorMessage)
                    .SetProperty(job => job.ProcessingStartedAt, (DateTimeOffset?)null)
                    .SetProperty(job => job.NextAttemptAt, (DateTimeOffset?)nextAttemptAt)
                    .SetProperty(job => job.CompletedAt, (DateTimeOffset?)null),
                ct);

        return returnedToPending > 0;
    }

    /// <inheritdoc />
    public async Task<int> CancelActiveForPullRequestAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        return await dbContext.ThreadPassJobs
            .Where(job => job.ClientId == clientId
                          && job.OrganizationUrl == organizationUrl
                          && job.ProjectId == projectId
                          && job.RepositoryId == repositoryId
                          && job.PullRequestId == pullRequestId
                          && InFlightStatuses.Contains(job.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ThreadPassJobStatus.Cancelled)
                    .SetProperty(job => job.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow),
                ct);
    }

    /// <inheritdoc />
    public async Task<StalledThreadPassSweep> ReclaimStalledAsync(
        TimeSpan stalledAfter,
        CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - stalledAfter;

        // A pass that died on its last attempt is failed rather than returned to pending. Pending it would be
        // dispatched by nothing, because the dispatch query skips rows at the attempt bound, while still
        // counting as in flight for the pull-request claim, so every later pass over that pull request would
        // be refused by a row no one will ever clear.
        var exhausted = await dbContext.ThreadPassJobs
            .Where(job => job.Status == ThreadPassJobStatus.Processing
                          && job.ProcessingStartedAt != null
                          && job.ProcessingStartedAt < cutoff
                          && job.AttemptCount >= ThreadPassJob.MaxAttempts)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ThreadPassJobStatus.Failed)
                    .SetProperty(job => job.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow)
                    .SetProperty(
                        job => job.ErrorMessage,
                        "The pass was abandoned mid-flight on its last permitted attempt."),
                ct);

        // The attempt this pass spent is not refunded: a pass that keeps dying mid-flight is exactly the case
        // the attempt bound exists for.
        var returnedToPending = await dbContext.ThreadPassJobs
            .Where(job => job.Status == ThreadPassJobStatus.Processing
                          && job.ProcessingStartedAt != null
                          && job.ProcessingStartedAt < cutoff
                          && job.AttemptCount < ThreadPassJob.MaxAttempts)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(job => job.Status, ThreadPassJobStatus.Pending)
                    .SetProperty(job => job.ProcessingStartedAt, (DateTimeOffset?)null),
                ct);

        return new StalledThreadPassSweep(returnedToPending, exhausted);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadPassHandledThreadKey>> GetHandledThreadKeysAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string revisionKey,
        CancellationToken ct = default)
    {
        // Scoped to the revision the asking pass runs at. Rows from earlier revisions say nothing about
        // whether this revision's code answers the finding, and reading them was what made a finding
        // assessable exactly once per pull request.
        var rows = await dbContext.ThreadPassHandledThreads
            .AsNoTracking()
            .Where(row => row.ClientId == clientId
                          && row.OrganizationUrl == organizationUrl
                          && row.ProjectId == projectId
                          && row.RepositoryId == repositoryId
                          && row.PullRequestId == pullRequestId
                          && row.RevisionKey == revisionKey)
            .Select(row => new { row.ThreadId, row.ObservedReplyCount })
            .ToListAsync(ct);

        return rows
            .Select(row => new ThreadPassHandledThreadKey(row.ThreadId, row.ObservedReplyCount, revisionKey))
            .ToList();
    }

    /// <inheritdoc />
    public async Task RecordHandledThreadAsync(
        Guid jobId,
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string threadId,
        int observedReplyCount,
        string revisionKey,
        CancellationToken ct = default)
    {
        var record = new ThreadPassHandledThread
        {
            Id = Guid.NewGuid(),
            ThreadPassJobId = jobId,
            ClientId = clientId,
            OrganizationUrl = organizationUrl,
            ProjectId = projectId,
            RepositoryId = repositoryId,
            PullRequestId = pullRequestId,
            ThreadId = threadId,
            ObservedReplyCount = observedReplyCount,
            RevisionKey = revisionKey,
            RecordedAt = DateTimeOffset.UtcNow,
        };

        dbContext.ThreadPassHandledThreads.Add(record);

        try
        {
            await dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The key is already on the record, which is the state this call was trying to reach. Writing it
            // twice is the answer to a retry that got as far as publishing, not an error.
            dbContext.Entry(record).State = EntityState.Detached;
        }
    }

    /// <summary>
    ///     Whether a failed write lost a race against a unique index, as opposed to failing for any other
    ///     reason.
    /// </summary>
    /// <remarks>
    ///     Only a uniqueness conflict means "someone else already recorded this", which is the one failure both
    ///     call sites are entitled to swallow. Treating every update failure as a lost race reports a
    ///     foreign-key violation, a check-constraint breach or an over-length value as ordinary contention and
    ///     silently discards the write.
    /// </remarks>
    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
    }

    private async Task<ThreadPassJob?> FindBlockingPassAsync(ThreadPassJob job, CancellationToken ct)
    {
        var contenders = await dbContext.ThreadPassJobs
            .AsNoTracking()
            .Where(candidate => candidate.ClientId == job.ClientId
                                && candidate.RepositoryId == job.RepositoryId
                                && candidate.PullRequestId == job.PullRequestId
                                && (InFlightStatuses.Contains(candidate.Status)
                                    || candidate.TriggerKey == job.TriggerKey))
            .ToListAsync(ct);

        // A pass still in flight owns the pull request; a pass that has already been here under this trigger
        // state would repeat its own work, which is the whole reason the state is named on the row. A pass
        // that ended having done nothing blocks neither, so it is not a contender.
        return contenders.FirstOrDefault(candidate => InFlightStatuses.Contains(candidate.Status))
               ?? contenders.FirstOrDefault(candidate =>
                   candidate.Status != ThreadPassJobStatus.Skipped
                   && string.Equals(candidate.TriggerKey, job.TriggerKey, StringComparison.Ordinal));
    }
}
