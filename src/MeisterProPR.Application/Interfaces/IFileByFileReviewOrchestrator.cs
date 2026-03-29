using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Orchestrates a pull request review by iterating through each changed file independently.
/// </summary>
public interface IFileByFileReviewOrchestrator
{
    /// <summary>
    ///     Performs a file-by-file AI review of the specified pull request.
    /// </summary>
    /// <param name="job">The current review job tracking state across retries.</param>
    /// <param name="pr">The pull request data to review.</param>
    /// <param name="baseContext">Shared context used for all per-file passes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="overrideClient">
    ///     Optional <see cref="IChatClient"/> to use for the synthesis pass instead of the
    ///     default DI-injected singleton. Used when a per-client AI connection is active.
    /// </param>
    /// <returns>The final aggregated review result.</returns>
    /// <exception cref="PartialReviewFailureException">Thrown if one or more file passes fail.</exception>
    Task<ReviewResult> ReviewAsync(ReviewJob job, PullRequest pr, ReviewSystemContext baseContext, CancellationToken ct, IChatClient? overrideClient = null);
}
