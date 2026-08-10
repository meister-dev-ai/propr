// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Authorizes proxied calls against the lease their caller presents.
///     <para>
///         The job's own state is the authority, read fresh on every call rather than trusted from a token
///         the caller carries. A lease can be reclaimed at any moment, and a caller that was legitimate a
///         second ago is exactly the caller this has to stop.
///     </para>
/// </summary>
public sealed class RunnerCallAuthorizer(IReviewJobExecutionStore jobs) : IRunnerCallAuthorizer
{
    /// <inheritdoc />
    public Task<RunnerCallAuthorization> AuthorizeAsync(RunnerCallContext call, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        var job = jobs.GetById(call.JobId);

        // A job that is not executing has nothing for an executor to do to it. That covers a job that
        // finished while a call was in flight as well as one that never existed.
        if (job is null || job.Status != JobStatus.Processing)
        {
            return Task.FromResult(RunnerCallAuthorization.Refuse(RunnerCallRefusal.JobNotExecuting));
        }

        // The generation is checked before the owner on purpose. A superseded caller and an impostor both
        // get refused, but they are different problems, and an operator reading the audit needs to see
        // which one happened.
        if (job.LeaseGeneration != call.Generation)
        {
            return Task.FromResult(RunnerCallAuthorization.Refuse(RunnerCallRefusal.SupersededGeneration));
        }

        if (!string.Equals(job.LeaseOwner, call.CallerIdentity, StringComparison.Ordinal))
        {
            return Task.FromResult(RunnerCallAuthorization.Refuse(RunnerCallRefusal.NotTheLeaseHolder));
        }

        return Task.FromResult(RunnerCallAuthorization.Allow(job.ClientId));
    }
}
