// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Code-level selection of which comment relevance filter implementation is wired into a review run.
/// </summary>
public sealed record CommentRelevanceFilterSelection(string? SelectedImplementationId)
{
    /// <summary>Default selection that preserves the pre-feature pipeline unchanged.</summary>
    public static CommentRelevanceFilterSelection None { get; } = new((string?)null);

    /// <summary>True when a named filter implementation has been selected.</summary>
    public bool HasSelection => !string.IsNullOrWhiteSpace(this.SelectedImplementationId);
}
