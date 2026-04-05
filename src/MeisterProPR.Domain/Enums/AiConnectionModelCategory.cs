// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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

    /// <summary>AI connection configured for embedding generation.</summary>
    Embedding = 3,

    /// <summary>Virtual category for out-of-loop memory reconsideration AI calls (not a real connection category).</summary>
    MemoryReconsideration = 4,

    /// <summary>Default category for connections with no explicit category (e.g., a basic AI connection). Used for token breakdown reporting.</summary>
    Default = 5,
}
