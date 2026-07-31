using MeisterDev.ProPR.CodeInsights.Rollups;

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.


namespace MeisterDev.ProPR.CodeInsights.Survival;

/// <summary>
///     Reads how much of what was raised survived to the newest increment of each pull request.
/// </summary>
public interface ICodeInsightSurvivalReader
{
    /// <summary>
    ///     Returns the survival counts over the query's window and scope, summed across its pull requests.
    /// </summary>
    Task<CodeInsightSurvivalCounts> GetSurvivalAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns per-pull-request survival, the ones that shed the most first: a pull request whose findings
    ///     mostly evaporated is the one worth reading.
    /// </summary>
    Task<IReadOnlyList<CodeInsightPullRequestSurvival>> GetSurvivalByPullRequestAsync(
        CodeInsightRollupQuery query,
        int topN,
        CancellationToken ct = default);
}
