namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Model category tag for an <c>AiConnection</c>.
///     Allows clients to configure a dedicated AI endpoint per file complexity tier.
///     When a connection with a matching category is present, the orchestrator uses it
///     instead of the default active connection for that tier.
/// </summary>
public enum AiConnectionModelCategory
{
    /// <summary>Connection used for low-complexity files (small diffs). Maps to <see cref="FileComplexityTier.Low" />.</summary>
    LowEffort = 0,

    /// <summary>Connection used for medium-complexity files. Maps to <see cref="FileComplexityTier.Medium" />.</summary>
    MediumEffort = 1,

    /// <summary>Connection used for high-complexity files (large diffs). Maps to <see cref="FileComplexityTier.High" />.</summary>
    HighEffort = 2,
}
