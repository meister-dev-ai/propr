// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>
///     Remembers which batches have been applied, in the database rather than in memory.
///     <para>
///         In memory would defeat the point. The failure this guards against is a control-plane restart in
///         the middle of a review, and a ledger that does not survive the restart would let every batch the
///         executor resends afterwards be applied a second time.
///     </para>
/// </summary>
public sealed class RunnerIngestLedger(MeisterProPRDbContext dbContext) : IRunnerIngestLedger
{
    /// <inheritdoc />
    public async Task<int> GetExpectedSequenceAsync(Guid jobId, CancellationToken ct = default)
    {
        var highest = await dbContext.RunnerIngestReceipts
            .AsNoTracking()
            .Where(receipt => receipt.JobId == jobId)
            .MaxAsync(receipt => (int?)receipt.Sequence, ct);

        return (highest ?? 0) + 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryRecordAsync(
        Guid jobId,
        int sequence,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        dbContext.RunnerIngestReceipts.Add(new RunnerIngestReceipt(jobId, sequence, idempotencyKey, DateTimeOffset.UtcNow));

        try
        {
            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // The unique index rejected it, which means another delivery of the same batch got there first.
            // That is the answer, not an error: whoever won it is applying the writes.
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ClearAsync(Guid jobId, CancellationToken ct = default)
    {
        await dbContext.RunnerIngestReceipts
            .Where(receipt => receipt.JobId == jobId)
            .ExecuteDeleteAsync(ct);
    }
}
