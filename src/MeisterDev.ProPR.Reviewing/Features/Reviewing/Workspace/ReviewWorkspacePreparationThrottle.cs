// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;

/// <summary>
///     Bounds how many workspace preparations run at the same time.
/// </summary>
/// <remarks>
///     One preparation fetches into a mirror and writes a complete checkout of the repository, so the number
///     running together decides how much of the workspace disk is being written at once. Without a bound, a
///     burst of jobs writes as many checkouts as there are jobs, and the disk runs out during checkout: "No
///     space left on device", a git child killed for memory, an index that could not be written. Each of
///     those fails the review outright. The per-repository mirror lock does not bound this, because a burst
///     across different repositories takes a different lock each.
/// </remarks>
internal sealed class ReviewWorkspacePreparationThrottle : IDisposable
{
    private readonly SemaphoreSlim _slots;

    public ReviewWorkspacePreparationThrottle(IOptions<ReviewWorkspaceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        this._slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentPreparations));
    }

    /// <summary>Waits for a preparation slot. Dispose the result to give the slot back.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await this._slots.WaitAsync(ct);
        return new Slot(this._slots);
    }

    public void Dispose()
    {
        this._slots.Dispose();
    }

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this._released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
