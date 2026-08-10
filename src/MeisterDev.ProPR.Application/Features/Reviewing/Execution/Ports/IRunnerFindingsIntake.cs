// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Takes the findings an executor produced and hands them to the control plane's publication.
///     <para>
///         Publication is the outward-facing step and it carries hard-won behaviour: deduplication at both
///         intake and publication, thread memory, posted-comment origins, and per-thread isolation so one
///         failing thread does not abort the rest. None of that is reimplemented here. What this adds is
///         only what a remote submitter needs: reassembling a chunked payload, refusing one that arrives
///         under a superseded lease, and making sure a resend cannot publish a second time.
///     </para>
/// </summary>
public interface IRunnerFindingsIntake
{
    /// <summary>Accepts one chunk of a submission, publishing once the last chunk arrives.</summary>
    Task<RunnerSubmissionResult> SubmitAsync(
        RunnerCallContext call,
        RunnerFindingsChunk chunk,
        CancellationToken ct = default);
}

/// <summary>
///     Publishes a completed review result. The seam that keeps a runner's findings and an in-process
///     review on one publication path instead of two.
/// </summary>
public interface IReviewResultPublisher
{
    /// <summary>Publishes a job's result through the intake, deduplication, and posting path.</summary>
    Task PublishAsync(Guid jobId, ReviewResult result, CancellationToken ct = default);
}
