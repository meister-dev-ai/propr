// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>
///     Claiming and liveness against PostgreSQL. Every operation here is a single conditional statement whose
///     predicate carries what the caller claims to be true, so the database decides the winner. Nothing is
///     resolved by reading a row, checking it in memory, and writing it back: two hosts doing that would both
///     be told they won.
/// </summary>
public sealed class ReviewJobLeaseStore(
    MeisterProPRDbContext dbContext,
    IJobRepository jobs,
    IOptions<ReviewLeaseOptions> leaseOptions,
    ILogger<ReviewJobLeaseStore> logger,
    IConcurrentReviewPeakStore? concurrentReviewPeaks = null) : IReviewJobLeaseStore
{
    /// <summary>
    ///     How long the protocol cleanup after a duration-ceiling failure may take before it is abandoned.
    /// </summary>
    /// <remarks>
    ///     The renewal that carries the stop directive does not return until this call does, and the
    ///     execution keeps working until it receives that directive. Short enough to stay well inside a
    ///     heartbeat interval, so a slow database delays the stop by a fraction of one renewal at most.
    /// </remarks>
    private static readonly TimeSpan ProtocolCleanupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The advisory lock that admits one claimant at a time to the installation-wide cap.</summary>
    private const string InstallationAdmissionLockKey = "propr:quota:concurrent-reviews";

    /// <inheritdoc />
    public Task<IReadOnlyList<ReviewJob>> GetClaimCandidatesAsync(
        int limit,
        DateTimeOffset? submittedAfter = null,
        CancellationToken ct = default)
    {
        return jobs.GetClaimCandidatesAsync(limit, submittedAfter, ct);
    }

    /// <inheritdoc />
    public async Task<ReviewJobLease?> TryClaimAsync(
        Guid jobId,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        var claim = await this.ClaimAsync(jobId, owner, leaseDuration, installationCap: null, ct).ConfigureAwait(false);
        return claim.Lease;
    }

    /// <inheritdoc />
    public async Task<ReviewJobCappedClaim> TryClaimWithinProcessingCapAsync(
        Guid jobId,
        string owner,
        TimeSpan leaseDuration,
        int cap,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);

        return await this.ClaimAsync(jobId, owner, leaseDuration, cap, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Claims a job within the installation-wide cap, when one is given, and within the cap of the job's
    ///     tenant, when the tenant sets one. A claim that neither cap applies to is the single unconditional
    ///     statement, so a tenant without a cap adds no locking to its claims.
    /// </summary>
    private async Task<ReviewJobCappedClaim> ClaimAsync(
        Guid jobId,
        string owner,
        TimeSpan leaseDuration,
        int? installationCap,
        CancellationToken ct)
    {
        var tenant = await this.ReadTenantCapAsync(jobId, ct).ConfigureAwait(false);
        if (tenant is { Cap: { } tenantCap } && tenant.ProcessingCount >= tenantCap)
        {
            // Refused without the admission below, which saves a full tenant the lock on every candidate. The count
            // was read without the tenant's lock, so a review that finished after the read can make this refusal
            // unnecessary, and the job waits one more tick. A count that is too low grants nothing here, because a
            // job is granted only inside the admission, which counts again.
            return ReviewJobCappedClaim.TenantAtCapacity(tenant.TenantId, tenantCap, tenant.ProcessingCount);
        }

        if (installationCap is null && tenant?.Cap is null)
        {
            return await this.ClaimUncappedAsync(jobId, owner, leaseDuration, ct).ConfigureAwait(false);
        }

        return await this.ClaimWithinCapsAsync(
            jobId,
            owner,
            leaseDuration,
            installationCap,
            tenant is { Cap: not null } ? tenant : null,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads the tenant of the job, the tenant's cap on concurrent reviews, and how many review jobs of the
    ///     tenant are processing. The count is read only for a tenant with a cap. Null when the job does not exist.
    /// </summary>
    private async Task<TenantCapRow?> ReadTenantCapAsync(Guid jobId, CancellationToken ct)
    {
        var rows = await dbContext.Database
            .SqlQueryRaw<TenantCapRow>(
                """
                SELECT c.tenant_id AS "TenantId",
                       t.max_concurrent_reviews AS "Cap",
                       CASE WHEN t.max_concurrent_reviews IS NULL THEN 0
                            ELSE (SELECT count(*)::int
                                  FROM review_jobs p
                                  JOIN clients pc ON pc.id = p.client_id
                                  WHERE p.status = 'Processing' AND pc.tenant_id = c.tenant_id)
                       END AS "ProcessingCount"
                FROM review_jobs j
                JOIN clients c ON c.id = j.client_id
                JOIN tenants t ON t.id = c.tenant_id
                WHERE j.id = {0}
                """,
                jobId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>The claim without any cap: one conditional statement and a read of what it stamped.</summary>
    private async Task<ReviewJobCappedClaim> ClaimUncappedAsync(
        Guid jobId,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken ct)
    {
        // One statement does all of it: the status move, the owner stamp, the generation bump, and the
        // expiry. The inner SELECT re-checks eligibility under a row lock and SKIP LOCKED turns a
        // concurrent claimant into a clean miss instead of a wait. Expiry comes from the database clock so
        // hosts with skewed clocks still agree on when a lease ends.
        var claimed = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET status = 'Processing',
                lease_owner = {1},
                lease_generation = lease_generation + 1,
                lease_expires_at = now() + make_interval(secs => {2}),
                last_heartbeat_at = now(),
                processing_started_at = now(),
                publishing_started_at = NULL
            WHERE id = (
                SELECT id FROM review_jobs
                WHERE id = {0} AND status = 'Pending'
                FOR UPDATE SKIP LOCKED
            )
            """,
            [jobId, owner, leaseDuration.TotalSeconds],
            ct).ConfigureAwait(false);

        if (claimed == 0)
        {
            return ReviewJobCappedClaim.NotClaimable;
        }

        // Read back what the claim just stamped. Only an expiry-driven reclaim could change these, and a
        // lease granted microseconds ago cannot already have expired.
        var granted = await this.ReadGrantedLeaseAsync(jobId, ct).ConfigureAwait(false);
        if (granted?.LeaseExpiresAt is null)
        {
            return ReviewJobCappedClaim.NotClaimable;
        }

        await this.ObserveConcurrentReviewPeakAsync().ConfigureAwait(false);

        return ReviewJobCappedClaim.Granted(new ReviewJobLease(jobId, owner, granted.LeaseGeneration, granted.LeaseExpiresAt.Value));
    }

    /// <summary>
    ///     The claim under the installation-wide cap, the tenant's cap, or both. Each cap is admitted under an
    ///     advisory lock of its own, and the claim counts inside both admissions.
    /// </summary>
    private async Task<ReviewJobCappedClaim> ClaimWithinCapsAsync(
        Guid jobId,
        string owner,
        TimeSpan leaseDuration,
        int? installationCap,
        TenantCapRow? tenant,
        CancellationToken ct)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        int claimed;
        var waitingForTenant = false;
        var waitingForInstallation = false;
        try
        {
            // Bounds how long this claimant waits for the locks below. A claimant that stalls while holding one
            // would otherwise block every other one indefinitely, and each of those waits occupies a pooled
            // connection for as long as it lasts. SET LOCAL applies to this transaction only.
            await dbContext.Database.ExecuteSqlRawAsync(
                "SET LOCAL lock_timeout = '5s'",
                ct).ConfigureAwait(false);

            // The advisory locks are what make the capacity conditions below mean anything. Each statement under
            // READ COMMITTED reads a snapshot taken when it starts, so two claimants of two different rows that
            // counted without a lock would both count the state from before either of them wrote, and both pass
            // a cap they jointly exceed. Row locking does not help: it arbitrates claims of the same row, and
            // these are different rows. A claimant that holds the lock counts in a statement that starts after
            // the previous holder committed, so the count includes that holder's claim. The locks are held for
            // the transaction, so they are released when the transaction ends, by commit or by rollback.
            //
            // The tenant's lock is taken first and the installation's second, by every claimant, so two
            // claimants never wait for each other in opposite order. A claimant whose tenant is full is turned
            // away before it queues for the installation-wide lock.
            if (tenant is not null)
            {
                waitingForTenant = true;
                await PostgresAdvisoryLocks.AcquireTransactionAsync(dbContext, TenantAdmissionLockKey(tenant.TenantId), ct)
                    .ConfigureAwait(false);
                waitingForTenant = false;
            }

            if (installationCap is not null)
            {
                waitingForInstallation = true;
                await PostgresAdvisoryLocks.AcquireTransactionAsync(dbContext, InstallationAdmissionLockKey, ct).ConfigureAwait(false);
                waitingForInstallation = false;
            }

            // The claim itself is the unbounded one, with the capacity conditions added. A condition whose flag
            // is false holds trivially. Everything the claim stamps is stamped identically, so a job claimed
            // under a cap is indistinguishable afterwards from one claimed without.
            claimed = await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE review_jobs
                SET status = 'Processing',
                    lease_owner = {1},
                    lease_generation = lease_generation + 1,
                    lease_expires_at = now() + make_interval(secs => {2}),
                    last_heartbeat_at = now(),
                    processing_started_at = now(),
                    publishing_started_at = NULL
                WHERE id = (
                    SELECT id FROM review_jobs
                    WHERE id = {0} AND status = 'Pending'
                    FOR UPDATE SKIP LOCKED
                )
                  AND (NOT {3} OR (SELECT count(*) FROM review_jobs WHERE status = 'Processing') < {4})
                  AND (NOT {5} OR (SELECT count(*)
                                   FROM review_jobs p
                                   JOIN clients pc ON pc.id = p.client_id
                                   WHERE p.status = 'Processing' AND pc.tenant_id = {6}) < {7})
                """,
                [
                    jobId,
                    owner,
                    leaseDuration.TotalSeconds,
                    installationCap is not null,
                    installationCap ?? 0,
                    tenant is not null,
                    tenant?.TenantId ?? Guid.Empty,
                    tenant?.Cap ?? 0,
                ],
                ct).ConfigureAwait(false);
        }
        // Only a timeout on one of the two admission locks is a statement about capacity. A timeout of the claim
        // statement itself, for example behind a table lock that a migration holds, propagates as the error it is.
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable &&
                                           (waitingForTenant || waitingForInstallation))
        {
            // A timeout on the installation-wide lock is reported as capacity rather than as an unclaimable job,
            // because the caller must stop scanning this tick: the next candidate would contend for the same lock
            // straight away and wait again. A timeout on the tenant's lock concerns this tenant only, so the
            // caller goes on with the jobs of other tenants. No executing count is carried, because this
            // claimant never entered the admission that counts them.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return waitingForTenant
                ? ReviewJobCappedClaim.TenantAtCapacity(tenant!.TenantId, tenant.Cap!.Value)
                : ReviewJobCappedClaim.AtCapacity(installationCap!.Value);
        }

        if (claimed == 0)
        {
            // Which refusal this is decides whether the caller's queue scan has anything left to try, so it is
            // read here, inside the same transaction, rather than left to a later query that would see a
            // different state. The installation-wide cap is checked first, because a full installation ends
            // the scan and a full tenant only skips the jobs of that tenant. The count is carried on the refusal
            // so the caller can report the number it was measured against.
            if (installationCap is { } cap)
            {
                var processing = await dbContext.ReviewJobs
                    .CountAsync(j => j.Status == JobStatus.Processing, ct)
                    .ConfigureAwait(false);
                if (processing >= cap)
                {
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return ReviewJobCappedClaim.AtCapacity(cap, processing);
                }
            }

            if (tenant is { Cap: { } tenantCap })
            {
                var tenantProcessing = await dbContext.ReviewJobs
                    .CountAsync(
                        j => j.Status == JobStatus.Processing
                             && dbContext.Clients.Any(client => client.Id == j.ClientId && client.TenantId == tenant.TenantId),
                        ct)
                    .ConfigureAwait(false);
                if (tenantProcessing >= tenantCap)
                {
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return ReviewJobCappedClaim.TenantAtCapacity(tenant.TenantId, tenantCap, tenantProcessing);
                }
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return ReviewJobCappedClaim.NotClaimable;
        }

        var granted = await this.ReadGrantedLeaseAsync(jobId, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        if (granted?.LeaseExpiresAt is null)
        {
            return ReviewJobCappedClaim.NotClaimable;
        }

        await this.ObserveConcurrentReviewPeakAsync().ConfigureAwait(false);

        return ReviewJobCappedClaim.Granted(new ReviewJobLease(jobId, owner, granted.LeaseGeneration, granted.LeaseExpiresAt.Value));
    }

    private async Task<GrantedLease?> ReadGrantedLeaseAsync(Guid jobId, CancellationToken ct)
    {
        return await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new GrantedLease(j.LeaseGeneration, j.LeaseExpiresAt))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>The advisory lock that admits one claimant at a time to the reviews of one tenant.</summary>
    private static string TenantAdmissionLockKey(Guid tenantId) =>
        $"propr:quota:tenant-concurrent-reviews:{tenantId:N}";

    /// <summary>
    ///     Records how many reviews are executing now against the current UTC day, after the claim that raised
    ///     the number has committed.
    /// </summary>
    /// <remarks>
    ///     Taken outside the claim's transaction because the record is descriptive: nothing decides what may
    ///     run from it, and a claim that succeeded must not be rolled back by a write that only feeds a report.
    ///     A concurrent claimant reading the same count is harmless, because the row keeps whichever count is
    ///     higher. The store is absent on a host that runs without the licensing module, where nothing reads
    ///     the record.
    ///     <para>
    ///         The caller's token is not passed on. The claim has committed by this point, so a cancellation
    ///         arriving now would leave the job executing while the caller was told it had claimed nothing.
    ///         A failure is logged and the next claim records again.
    ///     </para>
    /// </remarks>
    private async Task ObserveConcurrentReviewPeakAsync()
    {
        if (concurrentReviewPeaks is not { } peaks)
        {
            return;
        }

        try
        {
            await peaks.ObserveAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "The concurrent-review peak for the current day could not be recorded. The claim stands and the next one records again.");
        }
    }

    /// <inheritdoc />
    public async Task<ReviewJobLeaseRenewal> TryRenewAsync(
        ReviewJobLease lease,
        TimeSpan leaseDuration,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        // The predicate carries the guarantee: owner and generation must both still match, and the job must
        // still be executing. A holder that was reclaimed carries an older generation and renews nothing.
        // The last clause applies the ceiling on one execution. An execution continues only while its
        // renewals succeed, so refusing the renewal stops one that has run past the ceiling.
        //
        // A row without a start timestamp is renewed and stamped with one. The claim records the timestamp,
        // so this covers a row that reached Processing another way: accepting it unstamped would leave that
        // execution outside the ceiling for as long as it kept renewing, and refusing it would fail a job
        // over a missing timestamp. Stamping it starts the allowance from this renewal.
        var renewed = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET lease_expires_at = now() + make_interval(secs => {3}),
                last_heartbeat_at = now(),
                processing_started_at = COALESCE(processing_started_at, now())
            WHERE id = {0} AND lease_owner = {1} AND lease_generation = {2} AND status = 'Processing'
              AND (processing_started_at IS NULL
                   OR processing_started_at > now() - make_interval(secs => {4}))
            """,
            [
                lease.JobId,
                lease.Owner,
                lease.Generation,
                leaseDuration.TotalSeconds,
                leaseOptions.Value.MaxReviewDuration.TotalSeconds,
            ],
            ct);

        if (renewed == 0)
        {
            // A refusal has two very different causes, and the holder needs to know which. Either the job
            // was halted by a decision someone made about it, which it should report as that outcome, or it
            // simply no longer owns the job, in which case whoever does owns the outcome too.
            return await this.ExplainRefusedRenewalAsync(lease, ct).ConfigureAwait(false);
        }

        var expiresAt = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Id == lease.JobId)
            .Select(j => j.LeaseExpiresAt)
            .SingleOrDefaultAsync(ct);

        return new ReviewJobLeaseRenewal(true, expiresAt);
    }

    /// <summary>
    ///     Works out why a renewal was refused. The persisted job status is the cross-host source of truth
    ///     for a stop, a supersede, and a budget cut, so reading it here is what carries those decisions to
    ///     an execution running somewhere the process that made them cannot reach.
    /// </summary>
    private async Task<ReviewJobLeaseRenewal> ExplainRefusedRenewalAsync(
        ReviewJobLease lease,
        CancellationToken ct,
        bool allowDurationFailure = true)
    {
        var current = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Id == lease.JobId)
            .Select(j => new { j.Status, j.LeaseGeneration, j.LeaseOwner, j.ProcessingStartedAt })
            .SingleOrDefaultAsync(ct);

        if (current is null)
        {
            return ReviewJobLeaseRenewal.Rejected;
        }

        // The generation, not the owner, is what identifies this caller here. Reaching a terminal state
        // clears the owner precisely so nothing looks leased afterwards, and the generation is deliberately
        // left in place so a caller can still be recognised as the one that held it.
        if (current.LeaseGeneration != lease.Generation)
        {
            return ReviewJobLeaseRenewal.Rejected;
        }

        if (current.Status == JobStatus.Processing
            && !string.Equals(current.LeaseOwner, lease.Owner, StringComparison.Ordinal))
        {
            return ReviewJobLeaseRenewal.Rejected;
        }

        // The job is still executing, still owned by this caller, still on this generation, and the renewal
        // was refused. Every predicate of the renewal statement has been re-checked above except the duration
        // ceiling, so that is the clause that refused it. Deducing it leaves the database's clock as the only
        // clock involved; comparing the elapsed time here would introduce this process's clock as a second
        // one. A predicate added to that statement has to be re-checked above, or it will be reported as a
        // duration breach.
        if (allowDurationFailure && current.Status == JobStatus.Processing && current.ProcessingStartedAt is not null)
        {
            return await this.FailForMaxDurationAsync(lease, ct);
        }

        return current.Status switch
        {
            JobStatus.Completed => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.Completed),
            JobStatus.Stopped => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.OperatorStop),
            JobStatus.Superseded => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.Superseded),
            JobStatus.BudgetExceeded => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.BudgetCapReached),
            JobStatus.Cancelled => ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.OperatorStop),
            _ => ReviewJobLeaseRenewal.Rejected,
        };
    }

    /// <summary>
    ///     Fails the job for passing the ceiling on one execution, and only while this caller still holds it.
    /// </summary>
    /// <remarks>
    ///     The read that led here is not part of the statement that refused the renewal, so between the two
    ///     this process can be paused long enough for the lease to expire, another host to reclaim the job
    ///     and start a new generation, and this call to arrive afterwards. An unfenced terminal write would
    ///     then fail that host's running execution and clear its lease, so the transition carries the owner
    ///     and generation it believes it holds and is refused if either has moved on.
    ///     <para>
    ///         The statement carries the whole terminal state: the status, the classified reason, the message,
    ///         the completion stamp and the cleared lease. The classification is what tells an operator a
    ///         failed job was stopped for its duration rather than interrupted, which the other terminal
    ///         paths in this class record for the same purpose. Writing the status first and the rest afterwards would leave a job
    ///         that is terminal but still stamped with an owner, and with no reason recorded, if the second
    ///         write did not happen — which host shutdown, cancelling the token between the two, would cause.
    ///         The protocol cleanup that follows is not part of that state and runs uncancellable, so a
    ///         shutdown cannot stop it either.
    ///     </para>
    /// </remarks>
    private async Task<ReviewJobLeaseRenewal> FailForMaxDurationAsync(ReviewJobLease lease, CancellationToken ct)
    {
        var reason = $"The review ran longer than the {leaseOptions.Value.MaxReviewDurationMinutes}-minute "
                     + "ceiling on one execution and was stopped.";

        var fenced = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET status = 'Failed',
                failure_reason = {4},
                error_message = {3},
                completed_at = now(),
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_heartbeat_at = NULL,
                publishing_started_at = NULL
            WHERE id = {0} AND lease_owner = {1} AND lease_generation = {2} AND status = 'Processing'
            """,
            [lease.JobId, lease.Owner, lease.Generation, reason, (int)ReviewJobFailureReason.MaxDurationExceeded],
            ct);

        if (fenced == 0)
        {
            // Completion can commit between the refusal read and this conditional failure. Classify the
            // current generation without attempting another duration transition.
            return await this.ExplainRefusedRenewalAsync(lease, ct, allowDurationFailure: false);
        }

        // The row is terminal and unclaimable now, so this closes the protocols the execution left open
        // against a job nobody else can be holding. CancellationToken.None: the durable state is already
        // written, and a shutdown here would leave open protocol rows behind a completed transition.
        //
        // Failures are recorded and not propagated, and the wait is bounded. Throwing would reach the
        // heartbeat as a transient renewal error, which retries rather than stopping; blocking would hold the
        // heartbeat in this call. Either way the execution would keep running against a job the database
        // already shows as failed, and could still reach publication. Open protocol rows are the smaller
        // loss, and the durable state this stop rests on is already written.
        using var cleanupTimeout = new CancellationTokenSource(ProtocolCleanupTimeout);
        try
        {
            await jobs.SetFailedAsync(lease.JobId, reason, cleanupTimeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Review job {JobId} was failed for exceeding its duration ceiling, but the protocol cleanup that follows did not complete.",
                lease.JobId);
        }

        return ReviewJobLeaseRenewal.StoppedBecause(ReviewJobStopReason.MaxDurationExceeded);
    }

    /// <inheritdoc />
    public async Task<bool> TryReleaseAsync(ReviewJobLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        // A deliberate hand-back: the job returns to the claimable pool with its lease cleared. The
        // generation is left where it is, so the releasing party cannot come back and pass a fencing check.
        var released = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET status = 'Pending',
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_heartbeat_at = NULL,
                publishing_started_at = NULL
            WHERE id = {0} AND lease_owner = {1} AND lease_generation = {2} AND status = 'Processing'
            """,
            [lease.JobId, lease.Owner, lease.Generation],
            ct);

        return released > 0;
    }

    /// <inheritdoc />
    public async Task<bool> IsLeaseCurrentAsync(ReviewJobLease lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        return await dbContext.ReviewJobs
            .AsNoTracking()
            .AnyAsync(
                j => j.Id == lease.JobId
                     && j.LeaseOwner == lease.Owner
                     && j.LeaseGeneration == lease.Generation
                     && j.Status == JobStatus.Processing,
                ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpiredReviewJobLease>> GetExpiredLeasesAsync(
        int limit,
        TimeSpan reclaimBackoff,
        TimeSpan publicationTimeout,
        CancellationToken ct = default)
    {
        if (limit < 1)
        {
            return [];
        }

        // Expiry is compared against the database clock on both sides, so a host whose own clock runs fast
        // cannot decide another host's lease has ended early.
        var now = await this.GetDatabaseTimeAsync(ct).ConfigureAwait(false);
        var backoffCutoff = now - reclaimBackoff;
        var publishingCutoff = now - publicationTimeout;

        var rows = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Status == JobStatus.Processing
                        && j.LeaseExpiresAt != null
                        && j.LeaseExpiresAt < now
                        // Publication is protected by its own, longer timeout: reclaiming a job while its
                        // comments are going out is how a review gets posted twice.
                        && (j.PublishingStartedAt == null || j.PublishingStartedAt < publishingCutoff)
                        && (j.LastReclaimedAt == null || j.LastReclaimedAt < backoffCutoff))
            .OrderBy(j => j.LeaseExpiresAt)
            .Take(limit)
            .Select(j => new { j.Id, j.LeaseGeneration, j.LeaseExpiresAt })
            .ToListAsync(ct);

        return rows
            .Select(row => new ExpiredReviewJobLease(row.Id, row.LeaseGeneration, row.LeaseExpiresAt!.Value))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ReviewJobReclaimOutcome> TryReclaimAsync(
        ExpiredReviewJobLease expired,
        int maxConsecutiveReclaims,
        int maxTotalReclaims,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expired);

        // One statement decides both the counting and the outcome. Splitting "is the budget spent?" from
        // "take the job back" would let two hosts read the same counts and both act on them, and the
        // generation predicate is what makes a holder that recovered and renewed win over this sweep.
        var reclaimed = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET consecutive_reclaim_count = consecutive_reclaim_count + 1,
                total_reclaim_count = total_reclaim_count + 1,
                last_reclaimed_at = now(),
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_heartbeat_at = NULL,
                publishing_started_at = NULL,
                status = CASE
                    WHEN consecutive_reclaim_count + 1 > {2} OR total_reclaim_count + 1 > {3}
                    THEN 'Failed' ELSE 'Pending' END,
                failure_reason = CASE
                    WHEN consecutive_reclaim_count + 1 > {2} OR total_reclaim_count + 1 > {3}
                    THEN {4} ELSE failure_reason END,
                error_message = CASE
                    WHEN consecutive_reclaim_count + 1 > {2} OR total_reclaim_count + 1 > {3}
                    THEN {5} ELSE error_message END,
                completed_at = CASE
                    WHEN consecutive_reclaim_count + 1 > {2} OR total_reclaim_count + 1 > {3}
                    THEN now() ELSE completed_at END
            WHERE id = {0}
              AND lease_generation = {1}
              AND status = 'Processing'
              AND lease_expires_at IS NOT NULL
              AND lease_expires_at < now()
            """,
            [
                expired.JobId,
                expired.Generation,
                maxConsecutiveReclaims,
                maxTotalReclaims,
                (int)ReviewJobFailureReason.LeaseLost,
                "The review was interrupted more times than its reclaim budget allows without completing "
                + "further files. The last execution lost its lease and no attempt remains.",
            ],
            ct);

        if (reclaimed == 0)
        {
            return ReviewJobReclaimOutcome.NotReclaimed;
        }

        var status = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Id == expired.JobId)
            .Select(j => j.Status)
            .SingleAsync(ct);

        return status == JobStatus.Failed
            ? ReviewJobReclaimOutcome.FailedOutOfReclaimBudget
            : ReviewJobReclaimOutcome.Requeued;
    }

    /// <inheritdoc />
    public async Task<ReviewJobReclaimOutcome> TryReleaseFailedAsync(
        ReviewJobLease lease,
        int maxConsecutiveReclaims,
        int maxTotalReclaims,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        // The reclaim statement's twin, predicated on the releasing party's own lease instead of on
        // expiry: the holder is handing the job back because its attempt failed, and that spends the same
        // budget an expiry would have. last_reclaimed_at is stamped so the failure also respects the
        // reclaim backoff wherever expiry does.
        var released = await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE review_jobs
            SET consecutive_reclaim_count = consecutive_reclaim_count + 1,
                total_reclaim_count = total_reclaim_count + 1,
                last_reclaimed_at = now(),
                lease_owner = NULL,
                lease_expires_at = NULL,
                last_heartbeat_at = NULL,
                publishing_started_at = NULL,
                status = CASE
                    WHEN consecutive_reclaim_count + 1 > {3} OR total_reclaim_count + 1 > {4}
                    THEN 'Failed' ELSE 'Pending' END,
                failure_reason = CASE
                    WHEN consecutive_reclaim_count + 1 > {3} OR total_reclaim_count + 1 > {4}
                    THEN {5} ELSE failure_reason END,
                error_message = CASE
                    WHEN consecutive_reclaim_count + 1 > {3} OR total_reclaim_count + 1 > {4}
                    THEN {6} ELSE error_message END,
                completed_at = CASE
                    WHEN consecutive_reclaim_count + 1 > {3} OR total_reclaim_count + 1 > {4}
                    THEN now() ELSE completed_at END
            WHERE id = {0} AND lease_owner = {1} AND lease_generation = {2} AND status = 'Processing'
            """,
            [
                lease.JobId,
                lease.Owner,
                lease.Generation,
                maxConsecutiveReclaims,
                maxTotalReclaims,
                (int)ReviewJobFailureReason.LeaseLost,
                "The review failed more times than its reclaim budget allows without completing further "
                + "files. The last execution handed its lease back after a failure and no attempt remains.",
            ],
            ct);

        if (released == 0)
        {
            return ReviewJobReclaimOutcome.NotReclaimed;
        }

        var status = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Id == lease.JobId)
            .Select(j => j.Status)
            .SingleAsync(ct);

        return status == JobStatus.Failed
            ? ReviewJobReclaimOutcome.FailedOutOfReclaimBudget
            : ReviewJobReclaimOutcome.Requeued;
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkPublishingAsync(
        Guid jobId,
        ReviewJobLease? lease = null,
        CancellationToken ct = default)
    {
        var marked = lease is null
            ? await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE review_jobs
                SET publishing_started_at = now()
                WHERE id = {0} AND status = 'Processing'
                """,
                [jobId],
                ct)
            : await dbContext.Database.ExecuteSqlRawAsync(
                """
                UPDATE review_jobs
                SET publishing_started_at = now()
                WHERE id = {0} AND lease_owner = {1} AND lease_generation = {2} AND status = 'Processing'
                """,
                [jobId, lease.Owner, lease.Generation],
                ct);

        return marked > 0;
    }

    /// <inheritdoc />
    public async Task ClearPublishingAsync(Guid jobId, CancellationToken ct = default)
    {
        await dbContext.ReviewJobs
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.PublishingStartedAt, (DateTimeOffset?)null), ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> FailTimedOutPublicationsAsync(
        int limit,
        TimeSpan publicationTimeout,
        CancellationToken ct = default)
    {
        if (limit < 1)
        {
            return [];
        }

        var now = await this.GetDatabaseTimeAsync(ct).ConfigureAwait(false);
        var cutoff = now - publicationTimeout;

        var stuck = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Status == JobStatus.Processing
                        && j.PublishingStartedAt != null
                        && j.PublishingStartedAt < cutoff)
            .OrderBy(j => j.PublishingStartedAt)
            .Take(limit)
            .Select(j => j.Id)
            .ToListAsync(ct);

        var failed = new List<Guid>(stuck.Count);
        foreach (var jobId in stuck)
        {
            // Surfaced distinctly rather than reclaimed: publication may have posted some comments already,
            // so another attempt could duplicate them. This one needs a person to look at it.
            var affected = await dbContext.ReviewJobs
                .Where(j => j.Id == jobId
                            && j.Status == JobStatus.Processing
                            && j.PublishingStartedAt != null
                            && j.PublishingStartedAt < cutoff)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(j => j.Status, JobStatus.Failed)
                        .SetProperty(j => j.FailureReason, ReviewJobFailureReason.PublicationTimedOut)
                        .SetProperty(
                            j => j.ErrorMessage,
                            "Publication of this review began and did not finish within its timeout. Some "
                            + "comments may already have been posted, so it was not retried automatically.")
                        .SetProperty(j => j.CompletedAt, DateTimeOffset.UtcNow)
                        .SetProperty(j => j.LeaseOwner, (string?)null)
                        .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(j => j.LastHeartbeatAt, (DateTimeOffset?)null),
                    ct);

            if (affected > 0)
            {
                failed.Add(jobId);
            }
        }

        return failed;
    }

    /// <summary>
    ///     Reads the database clock. Every expiry decision is made against it rather than against a host
    ///     clock, so skew between hosts cannot make one of them consider another's lease finished early.
    /// </summary>
    private async Task<DateTimeOffset> GetDatabaseTimeAsync(CancellationToken ct)
    {
        var now = await dbContext.Database
            .SqlQueryRaw<DateTimeOffset>("SELECT now() AS \"Value\"")
            .SingleAsync(ct);
        return now;
    }

    /// <summary>The tenant of a job, the tenant's cap on concurrent reviews, and its processing review jobs.</summary>
    private sealed record TenantCapRow(Guid TenantId, int? Cap, int ProcessingCount);

    /// <summary>What a claim stamped, read back after it.</summary>
    private sealed record GrantedLease(int LeaseGeneration, DateTimeOffset? LeaseExpiresAt);
}
