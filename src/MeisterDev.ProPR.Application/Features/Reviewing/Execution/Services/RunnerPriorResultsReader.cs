// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Serves a resuming executor what its job already has recorded.
///     <para>
///         Authorized against the lease like every other call an executor makes. A job's file results carry
///         its findings, so answering an unauthorized caller would disclose another client's review.
///     </para>
/// </summary>
public sealed class RunnerPriorResultsReader(
    IRunnerCallAuthorizer authorizer,
    IReviewFileResultStore results) : IRunnerPriorResultsReader
{
    /// <inheritdoc />
    public async Task<RunnerToolResult<IReadOnlyList<RunnerPriorFileResult>>> ReadAsync(
        RunnerCallContext call,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var authorization = await authorizer.AuthorizeAsync(call, ct);
        if (!authorization.IsAuthorized)
        {
            return RunnerToolResult<IReadOnlyList<RunnerPriorFileResult>>.Refused(authorization.Refusal);
        }

        var job = await results.GetByIdWithFileResultsAsync(call.JobId, ct);
        if (job is null)
        {
            // Served, empty. The job existing is what the authorization above already established; a job
            // that comes back null here is a race with its deletion, and a fresh review is the safe read.
            return RunnerToolResult<IReadOnlyList<RunnerPriorFileResult>>.Served([]);
        }

        return RunnerToolResult<IReadOnlyList<RunnerPriorFileResult>>.Served(
        [
            .. job.FileReviewResults.Select(result => new RunnerPriorFileResult(
                result.FilePath,
                result.IsComplete,
                result.IsFailed,
                result.IsExcluded,
                result.ExclusionReason,
                result.ErrorMessage,
                result.PerFileSummary,
                [.. result.ReviewedPassKeys],
                result.Comments ?? [],
                // The flag survives the wire so synthesis suppresses carried candidates and labels the
                // carried files exactly as it would in process.
                result.IsCarriedForward))
        ]);
    }
}
