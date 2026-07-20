// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>
///     <see cref="AsyncLocal{T}" />-backed <see cref="IBudgetScopeAccessor" />. The scope set at the top of a
///     review job flows into the per-file review tasks it spawns, so every model call the job makes sees the same
///     shared scope. Registered as a singleton because the ambient value is per call context, not per instance.
/// </summary>
public sealed class BudgetScopeAccessor : IBudgetScopeAccessor
{
    private static readonly AsyncLocal<BudgetScope?> Ambient = new();

    /// <inheritdoc />
    public BudgetScope? Current => Ambient.Value;

    /// <inheritdoc />
    public IDisposable BeginScope(BudgetScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var previous = Ambient.Value;
        Ambient.Value = scope;
        return new ScopeHandle(previous);
    }

    private sealed class ScopeHandle(BudgetScope? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            Ambient.Value = previous;
        }
    }
}
