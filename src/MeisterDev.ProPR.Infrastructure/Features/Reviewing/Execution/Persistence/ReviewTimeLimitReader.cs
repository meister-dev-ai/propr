// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>
///     Reads the maximum review duration of a job's tenant from PostgreSQL, together with the time left until the
///     deadline. The time left is computed against <c>now()</c> in the same statement, because the claim stamped the
///     start of processing with the database clock and the clock of the asking host can differ from it.
/// </summary>
public sealed class ReviewTimeLimitReader(MeisterProPRDbContext dbContext) : IReviewTimeLimitReader
{
    /// <inheritdoc />
    public async Task<ReviewTimeLimitReading?> ReadAsync(Guid jobId, CancellationToken ct = default)
    {
        var rows = await dbContext.Database
            .SqlQueryRaw<TimeLimitRow>(
                """
                SELECT t.max_review_duration_seconds AS "MaxReviewDurationSeconds",
                       extract(epoch FROM j.processing_started_at
                                          + make_interval(secs => t.max_review_duration_seconds)
                                          - now())::double precision AS "RemainingSeconds"
                FROM review_jobs j
                JOIN clients c ON c.id = j.client_id
                JOIN tenants t ON t.id = c.tenant_id
                WHERE j.id = {0}
                  AND j.processing_started_at IS NOT NULL
                  AND t.max_review_duration_seconds IS NOT NULL
                """,
                jobId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Count == 0
            ? null
            : new ReviewTimeLimitReading(rows[0].MaxReviewDurationSeconds, TimeSpan.FromSeconds(rows[0].RemainingSeconds));
    }

    private sealed record TimeLimitRow(int MaxReviewDurationSeconds, double RemainingSeconds);
}
