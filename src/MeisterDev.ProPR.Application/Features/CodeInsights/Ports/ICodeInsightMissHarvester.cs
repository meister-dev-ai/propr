// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Events;

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

/// <summary>
///     Harvests human-authored review threads that ProPR did not raise, so recall (and therefore an honest
///     F1 rather than precision dressed up as quality) becomes computable.
/// </summary>
/// <remarks>
///     A passive observer on the same thread snapshots the review archive consumes. Best-effort: it never
///     throws into the crawl.
/// </remarks>
public interface ICodeInsightMissHarvester
{
    /// <summary>
    ///     Considers one observed thread. Threads the AI reviewer took part in are not candidates at all;
    ///     a human thread already harvested is left alone.
    /// </summary>
    Task HandleThreadObservedAsync(ThreadUpdatedEvent evt, CancellationToken ct = default);
}
