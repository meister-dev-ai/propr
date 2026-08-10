// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Gets a freshly leased job ready to be handed to a runner: the branch names the manifest names, and a
///     repository mirror this replica can serve.
///     <para>
///         Separated from the offer decision because it is the only expensive part. Choosing a candidate and
///         winning the claim are two cheap statements; cloning a repository is not, and keeping it behind its
///         own port is what lets the offer rules be tested without a git remote.
///     </para>
/// </summary>
public interface IRunnerJobDispatchPreparer
{
    /// <summary>
    ///     Prepares the mirror and gathers what the manifest needs, or explains why this job cannot be
    ///     dispatched. A failure here is a property of the job rather than of the runner, so the caller
    ///     releases the lease and tries the next candidate rather than refusing the runner.
    /// </summary>
    /// <param name="job">The job whose lease this runner just won.</param>
    /// <param name="lease">The lease it holds.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerJobDispatchPreparation> PrepareAsync(ReviewJob job, ReviewJobLease lease, CancellationToken ct = default);
}

/// <summary>The outcome of preparing a job for dispatch: either everything the manifest needs, or a reason.</summary>
public sealed record RunnerJobDispatchPreparation
{
    private RunnerJobDispatchPreparation(RunnerJobManifestRequest? request, string? failure)
    {
        this.Request = request;
        this.Failure = failure;
    }

    /// <summary>Everything the manifest resolver needs, or null when preparation failed.</summary>
    public RunnerJobManifestRequest? Request { get; }

    /// <summary>Why the job could not be prepared, or null on success.</summary>
    public string? Failure { get; }

    /// <summary>Whether preparation succeeded.</summary>
    public bool Succeeded => this.Request is not null;

    /// <summary>A prepared job.</summary>
    /// <param name="request">The manifest request.</param>
    public static RunnerJobDispatchPreparation Ready(RunnerJobManifestRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new RunnerJobDispatchPreparation(request, null);
    }

    /// <summary>A job that cannot be dispatched.</summary>
    /// <param name="failure">Operator-readable reason.</param>
    public static RunnerJobDispatchPreparation Failed(string failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);
        return new RunnerJobDispatchPreparation(null, failure);
    }
}
