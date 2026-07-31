// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Events;

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>
///     Passive consumer that materialises the findings a review increment produced into durable
///     code-insight records. It is a side-channel observer: it never participates in review decisions,
///     deduplication, memory, or the scope snapshot, and it only runs for a client whose collection is
///     licensed and opted in.
/// </summary>
public interface ICodeInsightFindingIngestionService
{
    /// <summary>
    ///     Materialises every finding carried by <paramref name="evt" /> as a durable record with a
    ///     surrogate identifier, touching the parent pull-request aggregate first. Re-processing the same
    ///     event leaves the finding set unchanged.
    /// </summary>
    Task HandleReviewFindingsProducedAsync(ReviewFindingsProducedEvent evt, CancellationToken ct = default);
}
