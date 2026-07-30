// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.History;

/// <summary>
///     Reads how much of the existing review history the collection has, so the size of the blind spot is a
///     number rather than a suspicion.
/// </summary>
/// <remarks>
///     Read-only and free: it counts rows that already exist and spends no model tokens and no provider calls.
/// </remarks>
public interface ICodeInsightHistoryReader
{
    /// <summary>
    ///     Returns per-repository coverage for the window, least covered first.
    /// </summary>
    Task<CodeInsightHistoryCoverage> GetCoverageAsync(
        CodeInsightHistoryCoverageQuery query,
        CancellationToken ct = default);
}
