using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Interfaces;

/// <summary>Core AI review contract. Accepts domain value objects only; returns domain value objects only.</summary>
public interface IAiReviewCore
{
    /// <summary>Performs an AI code review of the given pull request.</summary>
    Task<ReviewResult> ReviewAsync(PullRequest pullRequest, CancellationToken cancellationToken = default);
}