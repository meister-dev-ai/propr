// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Core AI review contract. Accepts domain and application value objects; returns domain value objects only.
/// </summary>
public interface IAiReviewCore
{
    /// <summary>Performs an AI code review of the given pull request.</summary>
    /// <param name="pullRequest">The pull request to review.</param>
    /// <param name="systemContext">Context carrying client instructions and review tools for the agentic loop.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ReviewResult> ReviewAsync(
        PullRequest pullRequest,
        ReviewSystemContext systemContext,
        CancellationToken cancellationToken = default);
}
