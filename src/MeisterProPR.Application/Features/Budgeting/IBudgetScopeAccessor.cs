// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>
///     Ambient access to the budget scope of the review job executing on the current logical call context. The
///     job opens a scope for its duration; the enforcing model-client decorators read it on each call. Concurrent
///     per-file review tasks that flow from the job share the one scope, so its running total is thread-safe.
/// </summary>
public interface IBudgetScopeAccessor
{
    /// <summary>The budget scope of the current review job, or <see langword="null" /> when none is active.</summary>
    BudgetScope? Current { get; }

    /// <summary>
    ///     Sets <paramref name="scope" /> as the ambient scope for the current call context and returns a handle
    ///     that restores the previous scope on dispose.
    /// </summary>
    IDisposable BeginScope(BudgetScope scope);
}
