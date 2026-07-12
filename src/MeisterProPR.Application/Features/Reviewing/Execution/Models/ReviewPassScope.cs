// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     The closed vocabulary of review-pass scopes. A <see langword="null" /> scope is the per-file default; a known
///     scope value selects where the pass runs relative to the change set. Persistence stores the raw string, so a new
///     scope is added by declaring a constant here and teaching the runtime to honor it — no schema change required.
/// </summary>
public static class ReviewPassScope
{
    /// <summary>
    ///     Per-file scope (the default). The pass runs once per changed file alongside the per-file baseline, unioning
    ///     its findings into that file's result.
    /// </summary>
    public const string PerFile = "per_file";

    /// <summary>
    ///     PR-wide scope. The pass runs once at the job level over the whole change set rather than per file, so the
    ///     per-file planner skips it.
    /// </summary>
    public const string PrWide = "pr_wide";

    /// <summary>All recognized scope values.</summary>
    public static IReadOnlyCollection<string> Known { get; } = [PerFile, PrWide];

    /// <summary>
    ///     True when <paramref name="scope" /> is <see langword="null" /> (the per-file default) or a recognized scope
    ///     value.
    /// </summary>
    public static bool IsValid(string? scope)
    {
        return scope is null || Known.Contains(scope);
    }
}
