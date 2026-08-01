// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Persistence for the index of findings already posted on a pull request.
/// </summary>
public interface IPostedFindingRepository
{
    /// <summary>
    ///     Returns the closest already-posted finding on this pull request whose cosine similarity to
    ///     <paramref name="queryVector" /> reaches <paramref name="minSimilarity" />, or
    ///     <see langword="null" /> when none does.
    /// </summary>
    Task<PostedFindingSimilarityDto?> FindClosestInPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        float[] queryVector,
        float minSimilarity,
        CancellationToken ct = default);

    /// <summary>
    ///     Inserts index rows, skipping any provider thread already indexed for its pull request so a
    ///     republished pass cannot duplicate a row.
    /// </summary>
    Task AddMissingAsync(IReadOnlyList<PostedFindingRecord> records, CancellationToken ct = default);
}
