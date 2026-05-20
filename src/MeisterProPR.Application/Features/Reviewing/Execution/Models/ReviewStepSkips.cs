// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Offline-only explicit skip list for review workflow steps that are normally always executed.
/// </summary>
public sealed class ReviewStepSkips
{
    private readonly HashSet<string> _skippedStepIds;

    /// <summary>
    ///     Creates a normalized skip set for one workflow execution.
    /// </summary>
    public ReviewStepSkips(IReadOnlyList<string>? skippedStepIds = null)
    {
        this.SkippedStepIds = skippedStepIds?
                                  .Where(stepId => !string.IsNullOrWhiteSpace(stepId))
                                  .Distinct(StringComparer.Ordinal)
                                  .ToArray()
                              ?? [];
        this._skippedStepIds = [.. this.SkippedStepIds];
    }

    /// <summary>
    ///     Stable step identifiers to omit for this offline execution.
    /// </summary>
    public IReadOnlyList<string> SkippedStepIds { get; }

    /// <summary>
    ///     Returns true when the specified step should be omitted.
    /// </summary>
    public bool Contains(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        return this._skippedStepIds.Contains(stepId);
    }
}
