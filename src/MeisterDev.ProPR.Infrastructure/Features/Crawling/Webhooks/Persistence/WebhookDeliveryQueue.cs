// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.Crawling.Webhooks.Persistence;

/// <summary>
///     The delivery queue, in PostgreSQL.
///     <para>
///         Claiming is one conditional statement with <c>FOR UPDATE SKIP LOCKED</c>, the same shape the
///         review-job lease uses: two replicas polling at the same instant take different rows rather than
///         waiting on each other, and a claim is never a read followed by a write that another caller can
///         interleave with.
///     </para>
///     <para>
///         Every timestamp is the database's own, never the host's: eligibility is written and compared in
///         the same clock, so replicas that disagree about the time still agree about the queue.
///     </para>
/// </summary>
public sealed class WebhookDeliveryQueue(MeisterProPRDbContext dbContext) : IWebhookDeliveryQueue
{
    /// <inheritdoc />
    public async Task<bool> EnqueueAsync(WebhookDeliveryQueueSubmission submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);

        // A provider retrying a delivery sends the same key. Recognised here rather than left to the
        // unique index, so the ordinary case is a query instead of a caught constraint violation.
        if (!string.IsNullOrWhiteSpace(submission.DeliveryKey))
        {
            var alreadyQueued = await dbContext.WebhookDeliveryQueue
                .AsNoTracking()
                .AnyAsync(
                    entry => entry.WebhookConfigurationId == submission.WebhookConfigurationId
                             && entry.DeliveryKey == submission.DeliveryKey,
                    ct);

            if (alreadyQueued)
            {
                return false;
            }
        }

        try
        {
            // Eligibility comes from the database clock, here and everywhere below, because it is compared
            // against the database clock when the row is claimed. Two replicas whose own clocks disagree
            // would otherwise enqueue deliveries that are either invisible until the skew passes or ahead
            // of everything already waiting.
            await dbContext.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO webhook_delivery_queue
                    (id, webhook_configuration_id, provider, path_key, event_type, delivery_key,
                     headers, payload, received_at, status, attempts, eligible_at)
                VALUES ({0}, {1}, {2}, {3}, {4}, NULLIF({5}, ''), CAST({6} AS jsonb), {7}, now(), 0, 0, now())
                """,
                [
                    Guid.NewGuid(),
                    submission.WebhookConfigurationId,
                    (int)submission.Provider,
                    submission.PathKey,
                    submission.EventType,

                    // Null rather than empty, so the unique index treats a provider that sends no delivery
                    // identifier, which is Azure DevOps, as having no key rather than as one shared key that
                    // would collapse every one of its deliveries into the first.
                    submission.DeliveryKey ?? string.Empty,
                    submission.HeadersJson,
                    submission.Payload,
                ],
                ct);

            return true;
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                  && !string.IsNullOrWhiteSpace(submission.DeliveryKey))
        {
            // Two deliveries of the same key arriving together: the index settles it and the loser reports
            // the delivery as already accepted, which is what it is.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<WebhookDeliveryQueueItem?> ClaimNextAsync(
        string owner,
        TimeSpan claimDuration,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        var claimed = await dbContext.Database
            .SqlQueryRaw<ClaimedRow>(
                """
                UPDATE webhook_delivery_queue
                SET status = 1,
                    attempts = attempts + 1,
                    claimed_by = {0},
                    claimed_at = now(),
                    eligible_at = now() + make_interval(secs => {1})
                WHERE id = (
                    SELECT id FROM webhook_delivery_queue
                    WHERE status = 0 AND eligible_at <= now()
                    ORDER BY eligible_at
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1
                )
                RETURNING id AS "Id",
                          provider AS "Provider",
                          path_key AS "PathKey",
                          headers AS "HeadersJson",
                          payload AS "Payload",
                          attempts AS "Attempts"
                """,
                owner,
                claimDuration.TotalSeconds)
            .ToListAsync(ct);

        var row = claimed.FirstOrDefault();
        return row is null
            ? null
            : new WebhookDeliveryQueueItem(
                row.Id,
                (ScmProvider)row.Provider,
                row.PathKey,
                row.HeadersJson,
                row.Payload,
                row.Attempts);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(Guid id, CancellationToken ct = default)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE webhook_delivery_queue
            SET status = 2, completed_at = now(), claimed_by = NULL
            WHERE id = {0}
            """,
            [id],
            ct);
    }

    /// <inheritdoc />
    public async Task FailAsync(
        Guid id,
        string error,
        int maxAttempts,
        TimeSpan backoff,
        CancellationToken ct = default)
    {
        // The database clock, like the claim above and for the same reason: two replicas with skewed
        // clocks must still agree on when a delivery becomes eligible, or one of them retries early and
        // the other never does.
        //
        // Kept rather than deleted once it is out of attempts: a delivery that never became a review is
        // the thing an operator has to be able to find, and the payload is what lets them replay it.
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE webhook_delivery_queue
            SET status = CASE WHEN attempts >= {2} THEN 3 ELSE 0 END,
                eligible_at = now() + make_interval(secs => {3}),
                last_error = {1},
                claimed_by = NULL
            WHERE id = {0}
            """,
            [id, Truncate(error), maxAttempts, backoff.TotalSeconds],
            ct);
    }

    /// <inheritdoc />
    public async Task<int> ReleaseExpiredClaimsAsync(CancellationToken ct = default)
    {
        // A replica that died holding a claim leaves the row Processing with an expiry behind it. Returned
        // to Pending without spending an attempt: nothing was learned about the delivery, only about the
        // host.
        return await dbContext.Database.ExecuteSqlRawAsync(
            """
            UPDATE webhook_delivery_queue
            SET status = 0, claimed_by = NULL
            WHERE status = 1 AND eligible_at <= now()
            """,
            ct);
    }

    private static string Truncate(string error)
    {
        return error.Length <= 2048 ? error : error[..2048];
    }

    private sealed record ClaimedRow(
        Guid Id,
        int Provider,
        string PathKey,
        string HeadersJson,
        string Payload,
        int Attempts);
}
