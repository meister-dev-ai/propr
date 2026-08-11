// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     A batch of trace events, per-file results, and spend that the control plane has already applied.
///     <para>
///         An executor spools its output locally and sends it in batches, so a network failure or a
///         control-plane restart means it resends. Without a record of what has already been written, a
///         resend writes the events twice and counts the spend twice, which is worse than losing them: a
///         duplicated cost cannot be reconciled afterwards.
///     </para>
///     <para>
///         The receipt also carries the sequence, because idempotency alone does not give ordering. An
///         executor that sent batch 5 and then batch 7 has lost batch 6, and the control plane has to report
///         that rather than apply 7 and leave a gap in the trace.
///     </para>
/// </summary>
public sealed class RunnerIngestReceipt
{
    private RunnerIngestReceipt()
    {
        this.IdempotencyKey = string.Empty;
    } // EF Core

    /// <summary>Records that a batch has been applied.</summary>
    /// <param name="jobId">The job the batch belongs to.</param>
    /// <param name="sequence">The batch's position in this job's stream, starting at 1.</param>
    /// <param name="idempotencyKey">The key the executor generated for this batch.</param>
    /// <param name="receivedAt">When the batch was applied.</param>
    public RunnerIngestReceipt(Guid jobId, int sequence, string idempotencyKey, DateTimeOffset receivedAt)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Batch sequence starts at 1.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        this.Id = Guid.NewGuid();
        this.JobId = jobId;
        this.Sequence = sequence;
        this.IdempotencyKey = idempotencyKey;
        this.ReceivedAt = receivedAt;
    }

    /// <summary>Unique identifier of this receipt.</summary>
    public Guid Id { get; init; }

    /// <summary>The job whose stream this batch belongs to.</summary>
    public Guid JobId { get; init; }

    /// <summary>The batch's position in the job's stream.</summary>
    public int Sequence { get; init; }

    /// <summary>The key the executor generated for the batch.</summary>
    public string IdempotencyKey { get; init; }

    /// <summary>When the batch was applied.</summary>
    public DateTimeOffset ReceivedAt { get; init; }
}
