// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Represents one named Reviewing pipeline composition bound to an existing persisted review strategy.
/// </summary>
public sealed record ReviewPipelineProfile
{
    /// <summary>
    ///     Initializes a Reviewing pipeline profile.
    /// </summary>
    public ReviewPipelineProfile(
        string profileId,
        string displayName,
        ReviewStrategy strategy,
        IReadOnlyList<string>? dispatchStageIds,
        IReadOnlyList<string>? perFileStageIds,
        IReadOnlyList<string>? finalizationStageIds,
        bool isBaseline,
        ReviewAggressiveness aggressiveness = ReviewAggressiveness.Balanced,
        int? qualityFilterThresholdOverride = null)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Profile id is required.", nameof(profileId));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        this.ProfileId = profileId;
        this.DisplayName = displayName;
        this.Strategy = strategy;
        this.DispatchStageIds = NormalizeStageIds(dispatchStageIds);
        this.PerFileStageIds = NormalizeStageIds(perFileStageIds);
        this.FinalizationStageIds = NormalizeStageIds(finalizationStageIds);
        this.IsBaseline = isBaseline;
        this.Aggressiveness = aggressiveness;
        this.QualityFilterThresholdOverride = qualityFilterThresholdOverride;
    }

    /// <summary>Stable internal profile identifier.</summary>
    public string ProfileId { get; }

    /// <summary>Human-readable profile name.</summary>
    public string DisplayName { get; }

    /// <summary>Persisted review strategy that owns this profile.</summary>
    public ReviewStrategy Strategy { get; }

    /// <summary>Ordered dispatch-stage identifiers.</summary>
    public IReadOnlyList<string> DispatchStageIds { get; }

    /// <summary>Ordered per-file-stage identifiers.</summary>
    public IReadOnlyList<string> PerFileStageIds { get; }

    /// <summary>Ordered finalization-stage identifiers.</summary>
    public IReadOnlyList<string> FinalizationStageIds { get; }

    /// <summary>True when this profile is the trusted baseline for its strategy.</summary>
    public bool IsBaseline { get; }

    /// <summary>
    ///     Controls how aggressively this profile emits and retains findings.
    ///     Assertive uses the emit-with-confidence certainty gate and LLM self-reflection ranking.
    ///     Calm/Balanced use the discard gate and deterministic ranking.
    /// </summary>
    public ReviewAggressiveness Aggressiveness { get; init; }

    /// <summary>
    ///     Optional override for the quality-filter threshold (<see cref="AiReviewOptions.QualityFilterThreshold" />).
    ///     When <see langword="null" />, the global option value is used.
    ///     Assertive → 1 (always filter at synthesis), Balanced → 10, Calm/baseline → null.
    /// </summary>
    public int? QualityFilterThresholdOverride { get; init; }

    private static IReadOnlyList<string> NormalizeStageIds(IReadOnlyList<string>? stageIds)
    {
        return stageIds?
                   .Where(stageId => !string.IsNullOrWhiteSpace(stageId))
                   .Distinct(StringComparer.Ordinal)
                   .ToArray()
               ?? [];
    }
}
