// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Product-owned AI purposes that resolve to configured models.
/// </summary>
public enum AiPurpose
{
    /// <summary>Default review generation.</summary>
    ReviewDefault = 0,

    /// <summary>Low-effort per-file review generation.</summary>
    ReviewLowEffort = 1,

    /// <summary>Medium-effort per-file review generation.</summary>
    ReviewMediumEffort = 2,

    /// <summary>High-effort per-file review and synthesis generation.</summary>
    ReviewHighEffort = 3,

    /// <summary>Thread-memory reconsideration chat calls.</summary>
    MemoryReconsideration = 4,

    /// <summary>Default embedding generation.</summary>
    EmbeddingDefault = 5,
}
