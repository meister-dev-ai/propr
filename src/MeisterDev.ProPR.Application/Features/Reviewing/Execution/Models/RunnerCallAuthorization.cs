// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     What an executor presents with every proxied call: which job it is calling about, which lease
///     generation it believes it holds, and who it is.
/// </summary>
/// <param name="JobId">The job the call concerns.</param>
/// <param name="Generation">The lease generation the caller holds.</param>
/// <param name="CallerIdentity">
///     The authenticated caller, which must match the lease owner. Registration issues this identity; until
///     then nothing populates it from a credential, which is exactly why the proxy endpoints are not
///     exposed over HTTP yet.
/// </param>
public sealed record RunnerCallContext(Guid JobId, int Generation, string CallerIdentity);

/// <summary>Why a proxied call was refused.</summary>
public enum RunnerCallRefusal
{
    /// <summary>The call was authorized.</summary>
    None = 0,

    /// <summary>No such job, or it is no longer executing.</summary>
    JobNotExecuting = 1,

    /// <summary>
    ///     The caller holds an older generation than the one on the job. It was reclaimed, and whoever holds
    ///     it now owns its outcome; serving this call would let two parties write the same review.
    /// </summary>
    SupersededGeneration = 2,

    /// <summary>The caller is not the party the lease was granted to.</summary>
    NotTheLeaseHolder = 3,
}

/// <summary>The outcome of authorizing a proxied call.</summary>
/// <param name="Refusal">Why the call was refused, or <see cref="RunnerCallRefusal.None" /> when it was allowed.</param>
/// <param name="ClientId">The client the job belongs to, resolved once so callers do not re-read it.</param>
public sealed record RunnerCallAuthorization(RunnerCallRefusal Refusal, Guid ClientId)
{
    /// <summary>Whether the call may be served.</summary>
    public bool IsAuthorized => this.Refusal == RunnerCallRefusal.None;

    /// <summary>An authorized call against the given client's job.</summary>
    public static RunnerCallAuthorization Allow(Guid clientId)
    {
        return new RunnerCallAuthorization(RunnerCallRefusal.None, clientId);
    }

    /// <summary>A refusal. Carries no client id, because a refused caller learns nothing about the job.</summary>
    public static RunnerCallAuthorization Refuse(RunnerCallRefusal refusal)
    {
        return new RunnerCallAuthorization(refusal, Guid.Empty);
    }
}
