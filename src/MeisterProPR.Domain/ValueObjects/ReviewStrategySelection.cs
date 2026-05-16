// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>Immutable strategy selection resolved at review intake time.</summary>
public sealed record ReviewStrategySelection(
    ReviewStrategy Strategy,
    ReviewStrategySelectionSource Source,
    ReviewComparisonMode ComparisonMode,
    ReviewPublicationMode PublicationMode,
    Guid? ComparisonGroupId,
    string? PipelineProfileId = null)
{
    /// <summary>Default selection that preserves existing review behavior.</summary>
    public static ReviewStrategySelection Default { get; } = new(
        ReviewStrategy.FileByFile,
        ReviewStrategySelectionSource.FallbackDefault,
        ReviewComparisonMode.Single,
        ReviewPublicationMode.Publish,
        null);
}
