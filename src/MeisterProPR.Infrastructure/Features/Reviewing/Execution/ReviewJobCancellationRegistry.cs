// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

/// <summary>
///     In-memory <see cref="IReviewJobCancellationRegistry" /> backed by a
///     <see cref="ConcurrentDictionary{TKey,TValue}" /> of per-job cancellation sources. Registered as a
///     singleton so the background worker and the control-plane stop endpoint share the same registrations.
/// </summary>
public sealed class ReviewJobCancellationRegistry : IReviewJobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _sources = new();

    /// <inheritdoc />
    public CancellationToken Register(Guid jobId)
    {
        var source = new CancellationTokenSource();

        // Atomic replace: a concurrent Cancel(jobId) always observes either the previous source or the new
        // one, never a transient removed/empty window. Any displaced source is disposed under the swap.
        this._sources.AddOrUpdate(
            jobId,
            source,
            (_, existing) =>
            {
                existing.Dispose();
                return source;
            });

        return source.Token;
    }

    /// <inheritdoc />
    public bool Cancel(Guid jobId)
    {
        if (!this._sources.TryGetValue(jobId, out var source))
        {
            return false;
        }

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The job finished and disposed its source between the lookup and the cancel; nothing to stop.
            return false;
        }
    }

    /// <inheritdoc />
    public void Remove(Guid jobId)
    {
        if (this._sources.TryRemove(jobId, out var source))
        {
            source.Dispose();
        }
    }
}
