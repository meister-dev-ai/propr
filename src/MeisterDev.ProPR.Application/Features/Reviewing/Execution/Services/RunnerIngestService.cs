// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Applies an executor's batched output exactly once, in order.
///     <para>
///         Three separate guarantees, and each of them matters on its own. Exactly once, because a resend that
///         writes the trace twice and counts the spend twice leaves a cost that cannot be reconciled. In
///         order, because idempotency does not give ordering and a trace with a gap in it is not readable.
///         Bounded, because the protocol payloads are already a known volume problem and batching them is
///         an easy way to make that worse.
///     </para>
/// </summary>
public sealed class RunnerIngestService(
    IRunnerCallAuthorizer authorizer,
    IRunnerIngestLedger ledger,
    IRunnerIngestWriter writer,
    IOptions<RunnerIngestOptions> options) : IRunnerIngestService
{
    /// <inheritdoc />
    public async Task<RunnerIngestResult> IngestAsync(
        RunnerCallContext call,
        RunnerIngestBatch batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.IdempotencyKey);

        var authorization = await authorizer.AuthorizeAsync(call, ct);
        if (!authorization.IsAuthorized)
        {
            return new RunnerIngestResult(RunnerIngestOutcome.NotAuthorized, 0, authorization.Refusal);
        }

        var limits = options.Value;
        if (batch.ItemCount > limits.MaxItemsPerBatch)
        {
            // Refused whole rather than partly applied. A half-applied batch would leave the executor
            // unable to say what still needs sending, which is the thing the sequence exists to avoid.
            return new RunnerIngestResult(
                RunnerIngestOutcome.TooLarge,
                await ledger.GetExpectedSequenceAsync(call.JobId, ct));
        }

        var expected = await ledger.GetExpectedSequenceAsync(call.JobId, ct);

        // Already applied. Answered as success, not as an error: a resend after a network failure is
        // ordinary, and an executor that had to special-case it would be carrying error-handling for the
        // most common thing that happens to it.
        if (batch.Sequence < expected)
        {
            return new RunnerIngestResult(RunnerIngestOutcome.AlreadyApplied, expected);
        }

        // A gap. Applying this would leave a hole in the job's trace, so the executor is told which batch
        // to resend from rather than left to discover the loss later.
        if (batch.Sequence > expected)
        {
            return new RunnerIngestResult(RunnerIngestOutcome.OutOfOrder, expected);
        }

        // The record is taken first and its uniqueness is what settles a race: two deliveries of the same
        // batch arriving together cannot both win it, so only one of them writes.
        if (!await ledger.TryRecordAsync(call.JobId, batch.Sequence, batch.IdempotencyKey, ct))
        {
            return new RunnerIngestResult(RunnerIngestOutcome.AlreadyApplied, expected + 1);
        }

        if (batch.Events.Count > 0)
        {
            await writer.WriteEventsAsync(call.JobId, batch.Events, ct);
        }

        if (batch.FileResults.Count > 0)
        {
            await writer.WriteFileResultsAsync(call.JobId, batch.FileResults, ct);
        }

        if (batch.Spend.Count > 0)
        {
            await writer.WriteSpendAsync(call.JobId, batch.Spend, ct);
        }

        return new RunnerIngestResult(RunnerIngestOutcome.Applied, batch.Sequence + 1);
    }
}
