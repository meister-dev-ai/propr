// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Run-level token-usage totals and breakdowns.
/// </summary>
public sealed record EvaluationTokenUsage(
    long TotalInputTokens,
    long TotalOutputTokens,
    IReadOnlyList<EvaluationTokenUsageBreakdown> ByModel,
    IReadOnlyList<EvaluationTokenUsageBreakdown> ByConnectionCategory);

/// <summary>
///     One token-usage breakdown bucket.
/// </summary>
public sealed record EvaluationTokenUsageBreakdown(
    string Key,
    long TotalInputTokens,
    long TotalOutputTokens);
