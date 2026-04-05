// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Represents the token cost contribution of a single effort-tier / model-ID combination within a review job.
///     Stored as a JSONB array in the <c>review_jobs.token_breakdown</c> column.
/// </summary>
/// <param name="ConnectionCategory">The AI connection category (effort tier) that generated these tokens.</param>
/// <param name="ModelId">The effective AI model deployment name (e.g. "gpt-4o", "gpt-4o-mini").</param>
/// <param name="TotalInputTokens">Accumulated input tokens for this tier/model combination.</param>
/// <param name="TotalOutputTokens">Accumulated output tokens for this tier/model combination.</param>
public sealed record TokenBreakdownEntry(
    AiConnectionModelCategory ConnectionCategory,
    string ModelId,
    long TotalInputTokens,
    long TotalOutputTokens);
