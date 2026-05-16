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
        bool isBaseline)
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

    private static IReadOnlyList<string> NormalizeStageIds(IReadOnlyList<string>? stageIds)
    {
        return stageIds?
                   .Where(stageId => !string.IsNullOrWhiteSpace(stageId))
                   .Distinct(StringComparer.Ordinal)
                   .ToArray()
               ?? [];
    }
}
