// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>
///     Reads the individual records behind a metric, so any number on a view can be opened up.
/// </summary>
public interface ICodeInsightBrowseReader
{
    /// <summary>
    ///     Returns the findings inside the query's scope, newest review first. Both the outcome and the type
    ///     narrowings are optional, so the same read serves "show me this week's findings", "show me the
    ///     false positives", and "show me the concurrency findings".
    /// </summary>
    Task<IReadOnlyList<CodeInsightFindingRow>> ListFindingsAsync(
        CodeInsightBrowseQuery query,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the harvested human threads inside the query's scope, newest first, qualifying and
    ///     non-qualifying alike.
    /// </summary>
    Task<IReadOnlyList<CodeInsightMissRow>> ListMissesAsync(
        CodeInsightBrowseQuery query,
        CancellationToken ct = default);
}
