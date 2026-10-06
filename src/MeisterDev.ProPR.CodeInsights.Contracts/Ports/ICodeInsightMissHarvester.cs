// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Events;

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>
///     Retains human-authored review concerns and collection failures for observed recall evidence.
/// </summary>
/// <remarks>
///     A passive observer on the same thread snapshots the review archive consumes. Best-effort: it never
///     throws into the crawl.
/// </remarks>
public interface ICodeInsightMissHarvester
{
    /// <summary>
    ///     Considers one observed thread, excludes reviewer-owned threads, and refreshes changed human
    ///     evidence with bounded judgement and dimension-enrichment retries.
    /// </summary>
    /// <returns>True for a retained observation or an intentional exclusion; false for an unretained opportunity.</returns>
    Task<bool> HandleThreadObservedAsync(ThreadUpdatedEvent evt, CancellationToken ct = default);
}
