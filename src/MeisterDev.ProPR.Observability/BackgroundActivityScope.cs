// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Observability;

/// <summary>
///     Marks the calling asynchronous flow as unattended background work so that per-request outbound
///     HTTP trace spans can be dropped for it.
/// </summary>
/// <remarks>
///     <para>
///         Recurring pollers fan out over provider APIs on a fixed interval whether or not anything
///         changed, so a span per outbound request grows without bound while carrying almost no
///         diagnostic value. Wrapping a poll cycle in this scope suppresses those spans; the aggregate
///         view survives because the metrics pipeline keeps counting every request, and the cycle's own
///         domain span still records duration and outcome.
///     </para>
///     <para>
///         Only the trace pipeline consults this scope. Metric instrumentation is registered separately
///         and is deliberately left untouched.
///     </para>
/// </remarks>
public static class BackgroundActivityScope
{
    private static readonly AsyncLocal<int> Depth = new();

    /// <summary>Gets a value indicating whether the calling flow is inside a background scope.</summary>
    public static bool IsActive => Depth.Value > 0;

    /// <summary>Enters a background scope for the calling flow until the returned handle is disposed.</summary>
    /// <returns>A handle that restores the previous scope depth when disposed.</returns>
    public static IDisposable Begin()
    {
        return new Scope();
    }

    /// <summary>
    ///     Restores the enclosing depth rather than decrementing, so a double dispose cannot leak the
    ///     suppression into sibling work that reuses the execution context.
    /// </summary>
    private sealed class Scope : IDisposable
    {
        private readonly int previousDepth;
        private bool disposed;

        internal Scope()
        {
            this.previousDepth = Depth.Value;
            Depth.Value = this.previousDepth + 1;
        }

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            Depth.Value = this.previousDepth;
        }
    }
}
