// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Holds each leased job's review-context tools for the life of its lease.
///     <para>
///         Process-local on purpose. The tools are a live object graph holding provider clients, so they
///         cannot be shared between control-plane replicas anyway; what makes that safe is that the runner
///         calls the replica that granted its lease, and a replica that does not hold the job answers that
///         it does not, which the caller handles the same way it handles losing its lease.
///     </para>
/// </summary>
public sealed class RunnerJobToolsRegistry : IRunnerJobToolsRegistry
{
    private readonly ConcurrentDictionary<Guid, RunnerJobTools> _byJob = new();

    /// <inheritdoc />
    public void Register(Guid jobId, IReviewContextTools tools, bool codeKnowledgeOffered)
    {
        ArgumentNullException.ThrowIfNull(tools);
        this._byJob[jobId] = new RunnerJobTools(tools, codeKnowledgeOffered);
    }

    /// <inheritdoc />
    public RunnerJobTools? Find(Guid jobId)
    {
        return this._byJob.TryGetValue(jobId, out var held) ? held : null;
    }

    /// <inheritdoc />
    public void Release(Guid jobId)
    {
        this._byJob.TryRemove(jobId, out _);
    }
}
