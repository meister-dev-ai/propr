// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>
///     Writes an executor's batched output through the persistence an in-process review already uses.
///     <para>
///         Nothing here is a parallel path. Trace events go to the protocol recorder, per-file outcomes
///         become the same <see cref="ReviewFileResult" /> rows that make resume possible, and spend is
///         accrued by closing a protocol, which is the one route that moves tokens onto a job's totals and
///         the client's usage sample. A second way of writing any of these would drift from the first.
///     </para>
/// </summary>
public sealed partial class RunnerIngestWriter(
    IJobRepository jobs,
    IProtocolRecorder protocolRecorder,
    ILogicalModelResolver? logicalModels = null,
    ILogger<RunnerIngestWriter>? logger = null) : IRunnerIngestWriter
{
    /// <inheritdoc />
    public async Task WriteEventsAsync(
        Guid jobId,
        IReadOnlyList<RunnerTraceEvent> events,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        // One protocol per batch, so a remote review's trace reads as a sequence of passes the viewer
        // already knows how to render rather than as a new kind of record.
        var protocolId = await protocolRecorder.BeginAsync(jobId, 1, "runner-trace", ct: ct);

        foreach (var traceEvent in events)
        {
            ct.ThrowIfCancellationRequested();
            await protocolRecorder.RecordReviewStrategyEventAsync(
                protocolId,
                traceEvent.Name,
                traceEvent.Details,
                traceEvent.Details,
                null,
                ct);
        }

        await protocolRecorder.SetCompletedAsync(protocolId, "Completed", 0, 0, 0, 0, null, ct);
    }

    /// <inheritdoc />
    public async Task WriteFileResultsAsync(
        Guid jobId,
        IReadOnlyList<RunnerFileOutcome> results,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(results);

        var existing = await jobs.GetByIdWithFileResultsAsync(jobId, ct);
        var byPath = existing?.FileReviewResults.ToDictionary(r => r.FilePath, StringComparer.Ordinal)
                     ?? [];

        // A file can appear more than once in one batch: the executor reports an outcome when a file
        // finishes and again when it is revised, and both are buffered. Only the last one is written —
        // inserting each of them violates the one-row-per-file constraint and takes the whole batch down,
        // spend and trace included.
        var lastPerPath = results
            .GroupBy(outcome => outcome.FilePath, StringComparer.Ordinal)
            .Select(group => group.Last());

        foreach (var outcome in lastPerPath)
        {
            ct.ThrowIfCancellationRequested();

            if (byPath.TryGetValue(outcome.FilePath, out var already))
            {
                if (already.IsComplete || already.IsExcluded)
                {
                    // Already checkpointed. A replayed batch must not overwrite a finished file, or a
                    // resume would lose the result it was supposed to protect.
                    continue;
                }

                // A row this job already has, not yet finished: updated in place. Adding a second row for
                // the same file is what the unique index exists to prevent. A failed row may be upgraded
                // by a retry, and the entity requires the earlier mark cleared first.
                if (already.IsFailed)
                {
                    already.ResetForRetry();
                }

                Apply(outcome, already);
                await jobs.UpdateFileResultAsync(already, ct);
                continue;
            }

            var result = new ReviewFileResult(jobId, outcome.FilePath);
            Apply(outcome, result);
            await jobs.AddFileResultAsync(result, ct);
            byPath[outcome.FilePath] = result;
        }
    }

    private static void Apply(RunnerFileOutcome outcome, ReviewFileResult result)
    {
        // Exclusion is the stronger statement — an excluded file was never reviewed — and the entity
        // refuses to carry two marks, so it is checked first, exactly as the executor's own seeding does.
        if (outcome.IsExcluded)
        {
            result.MarkExcluded(outcome.ExclusionReason ?? "excluded");
        }
        else if (outcome.IsFailed)
        {
            result.MarkFailed(outcome.ErrorMessage ?? "The executor reported this file as failed.");
        }
        else if (outcome.IsComplete)
        {
            result.MarkCompleted(outcome.Summary ?? string.Empty, outcome.Comments ?? [], outcome.ReviewedPassKeys);
        }
    }

    /// <inheritdoc />
    public async Task WriteSpendAsync(
        Guid jobId,
        IReadOnlyList<RunnerSpendRecord> spend,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spend);
        if (spend.Count == 0)
        {
            return;
        }

        var clientId = jobs.GetById(jobId)?.ClientId;
        var physicalModelByName = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var record in spend)
        {
            ct.ThrowIfCancellationRequested();

            // Opening and closing a protocol is what accrues tokens onto the job and the client's usage
            // sample. Writing the totals directly would bypass the accrual the pricing pass reads. The
            // physical model rides along when the logical name still resolves, because pricing is per
            // physical model: a protocol carrying only the logical name priced against nothing, and a
            // remote review's cost stayed null however much it spent.
            var protocolId = await protocolRecorder.BeginAsync(
                jobId,
                1,
                $"runner-relay:{record.LogicalModelName}",
                modelId: await this.TryResolvePhysicalModelAsync(clientId, record.LogicalModelName, physicalModelByName, ct),
                ct: ct,
                logicalModelName: record.LogicalModelName);

            await protocolRecorder.SetCompletedAsync(
                protocolId,
                "Completed",
                record.InputTokens,
                record.OutputTokens,
                0,
                0,
                null,
                ct);
        }
    }

    /// <summary>
    ///     The physical model behind a logical name, resolved once per name per batch. Fail-soft: a name
    ///     whose binding is gone still records its tokens — unpriced beats unrecorded, the tokens being
    ///     already spent — which is the same best-effort rule the pricing pass itself follows.
    /// </summary>
    private async Task<string?> TryResolvePhysicalModelAsync(
        Guid? clientId,
        string logicalModelName,
        Dictionary<string, string?> cache,
        CancellationToken ct)
    {
        if (logicalModels is null || clientId is null || string.IsNullOrWhiteSpace(logicalModelName))
        {
            return null;
        }

        if (cache.TryGetValue(logicalModelName, out var known))
        {
            return known;
        }

        string? physicalModelId = null;
        try
        {
            var resolved = await logicalModels.ResolveChatRuntimeAsync(clientId.Value, logicalModelName, ct: ct);
            physicalModelId = resolved.Runtime.Model.RemoteModelId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger is not null)
            {
                LogPhysicalModelUnresolved(logger, logicalModelName, ex.Message);
            }
        }

        cache[logicalModelName] = physicalModelId;
        return physicalModelId;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The logical model '{LogicalModelName}' behind an ingested spend record no longer resolves; its tokens are recorded unpriced: {Reason}")]
    private static partial void LogPhysicalModelUnresolved(ILogger logger, string logicalModelName, string reason);
}
