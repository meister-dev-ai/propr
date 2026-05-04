// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;

/// <summary>
///     Curated Reviewing-owned persistence invariant facts.
/// </summary>
public sealed class PersistenceReviewInvariantFactProvider : IReviewInvariantFactProvider
{
    public const string ReviewFileResultsUniqueJobPathInvariantId = InvariantFact.ReviewFileResultsUniqueJobPathInvariantId;

    public IReadOnlyList<InvariantFact> GetFacts()
    {
        return
        [
            new InvariantFact(
                ReviewFileResultsUniqueJobPathInvariantId,
                InvariantFact.PersistenceFamily,
                "Review file result uniqueness",
                "EF metadata / review_file_results unique index",
                "unique(job_id,file_path)",
                "The review_file_results table enforces a unique row per (job_id, file_path)."),
        ];
    }
}
