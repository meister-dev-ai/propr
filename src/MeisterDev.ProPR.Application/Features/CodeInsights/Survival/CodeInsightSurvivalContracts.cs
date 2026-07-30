// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Rollups;

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Survival;

/// <summary>
///     How much of what a review raised was still being raised when the pull request finished.
/// </summary>
/// <remarks>
///     <para>
///         The distinction that matters: a problem raised once and never again told you little, whereas a problem
///         still reported three increments later is a durable statement about the code. Counting only what remains
///         is what separates the two.
///     </para>
///     <para>
///         Of what stopped being reported, the ones with a corroborated fix are separated from the ones that
///         simply stopped appearing. The first is the reviewer working; the second is either the code moving out
///         from under the finding or the reviewer being inconsistent, and lumping them together would flatter it.
///     </para>
/// </remarks>
/// <param name="Persisted">Problems still being raised at the newest increment.</param>
/// <param name="Fixed">Problems that stopped being raised and carry a corroborated fix.</param>
/// <param name="Dropped">Problems that stopped being raised with nothing to show for it.</param>
/// <param name="PullRequests">How many pull requests these counts come from.</param>
public readonly record struct CodeInsightSurvivalCounts(int Persisted, int Fixed, int Dropped, int PullRequests)
{
    /// <summary>Every problem raised, however it ended.</summary>
    public int Total => this.Persisted + this.Fixed + this.Dropped;

    /// <summary>
    ///     The share of raised problems still standing at the end, or <see langword="null" /> when nothing was
    ///     raised. Undefined rather than zero, like every other ratio in this slice.
    /// </summary>
    public double? PersistenceRate => this.Total == 0 ? null : (double)this.Persisted / this.Total;
}

/// <summary>Survival counts for one pull request, so the aggregate can be opened up.</summary>
/// <param name="ClientId">Owning client.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Provider pull-request identifier.</param>
/// <param name="Revisions">How many increments were collected for it.</param>
/// <param name="Counts">Its own survival counts.</param>
public sealed record CodeInsightPullRequestSurvival(
    Guid ClientId,
    string RepositoryId,
    long PullRequestId,
    int Revisions,
    CodeInsightSurvivalCounts Counts,
    string? RepositoryName = null);
