// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     Result of looking a candidate finding up against the findings already posted on its pull request.
/// </summary>
public sealed record PostedFindingMatchDto
{
    /// <summary>True when an earlier increment already posted this concern.</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>Provider thread carrying the finding this candidate repeats, when one matched.</summary>
    public string? ProviderThreadId { get; init; }

    /// <summary>Index record that matched, when one did.</summary>
    public Guid? PostedFindingId { get; init; }

    /// <summary>
    ///     Cosine similarity between the candidate and the matched finding. Recorded whether or not the match
    ///     is acted on, because the false-suppression rate cannot be judged without it.
    /// </summary>
    public float? SimilarityScore { get; init; }

    /// <summary>
    ///     How close the closest already-posted finding came when it was not close enough to act on, and which
    ///     thread that was. Recorded so a threshold set too high is visible: without the misses, only the
    ///     suppressions have scores and the line can only ever be shown to be too low.
    /// </summary>
    public float? NearMissScore { get; init; }

    /// <summary>Thread the near miss belongs to, when there was one.</summary>
    public string? NearMissProviderThreadId { get; init; }

    /// <summary>Named components that were unavailable during the lookup.</summary>
    public IReadOnlyList<string> DegradedComponents { get; init; } = [];

    /// <summary>Human-readable cause describing why the lookup could not run in full.</summary>
    public string? DegradedCause { get; init; }

    /// <summary>Whether ProPR closed the matched thread itself rather than a reviewer closing it.</summary>
    public bool AutoResolvedByProPr { get; init; }

    /// <summary>True when any component of the lookup was unavailable.</summary>
    public bool IsDegraded => this.DegradedComponents.Count > 0;

    /// <summary>Returns a non-matching result, preserving any degraded-mode metadata.</summary>
    public static PostedFindingMatchDto NoMatch(
        IReadOnlyList<string>? degradedComponents = null,
        string? degradedCause = null)
    {
        return new PostedFindingMatchDto
        {
            DegradedComponents = degradedComponents ?? [],
            DegradedCause = degradedCause,
        };
    }

    /// <summary>Returns a non-matching result that carries how close the nearest posted finding came.</summary>
    public static PostedFindingMatchDto NearMiss(string providerThreadId, float similarityScore)
    {
        return new PostedFindingMatchDto
        {
            NearMissProviderThreadId = providerThreadId,
            NearMissScore = similarityScore,
        };
    }

    /// <summary>Returns a match against an already-posted finding.</summary>
    public static PostedFindingMatchDto Match(
        string providerThreadId,
        Guid postedFindingId,
        float similarityScore,
        bool autoResolvedByProPr = false)
    {
        return new PostedFindingMatchDto
        {
            IsDuplicate = true,
            ProviderThreadId = providerThreadId,
            PostedFindingId = postedFindingId,
            SimilarityScore = similarityScore,
            AutoResolvedByProPr = autoResolvedByProPr,
        };
    }
}

/// <summary>
///     The closest already-posted finding a similarity search returned.
/// </summary>
/// <param name="PostedFindingId">Index record identifier.</param>
/// <param name="ProviderThreadId">Provider thread the indexed finding was posted as.</param>
/// <param name="SimilarityScore">Cosine similarity to the query.</param>
/// <param name="AutoResolvedByProPr">Whether ProPR closed that thread itself.</param>
public sealed record PostedFindingSimilarityDto(
    Guid PostedFindingId,
    string ProviderThreadId,
    float SimilarityScore,
    bool AutoResolvedByProPr = false);

/// <summary>
///     One finding a review job posted, handed to the index so later increments can recognise it.
/// </summary>
/// <param name="ClientId">Owning client.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Pull request the finding was posted on.</param>
/// <param name="ProviderThreadId">Provider thread the finding was posted as.</param>
/// <param name="ReviewJobId">Review job that posted it.</param>
/// <param name="IterationId">Provider iteration it was posted against.</param>
/// <param name="FilePath">File it was anchored to, or null for a pull-request-level finding. Context only.</param>
/// <param name="Severity">Severity it carried. Context only.</param>
/// <param name="FindingMessage">The finding text as the model wrote it, without any severity prefix.</param>
public sealed record PostedFindingEntry(
    Guid ClientId,
    string RepositoryId,
    int PullRequestId,
    string ProviderThreadId,
    Guid ReviewJobId,
    int IterationId,
    string? FilePath,
    CommentSeverity Severity,
    string FindingMessage,
    bool AutoResolvedByProPr = false);
