// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Product-owned AI purposes that resolve to configured models.
/// </summary>
public enum AiPurpose
{
    /// <summary>Default review generation.</summary>
    ReviewDefault = 0,

    /// <summary>Diff-based ProRV relevance prefiltering for focused review guidance.</summary>
    [JsonStringEnumMemberName("proRvPrefilter")]
    ProRVPrefilter = 1,

    /// <summary>Low-effort per-file review generation.</summary>
    ReviewLowEffort = 2,

    /// <summary>Medium-effort per-file review generation.</summary>
    ReviewMediumEffort = 3,

    /// <summary>High-effort per-file review and synthesis generation.</summary>
    ReviewHighEffort = 4,

    /// <summary>Thread-memory reconsideration chat calls.</summary>
    MemoryReconsideration = 5,

    /// <summary>Default embedding generation.</summary>
    EmbeddingDefault = 6,

    /// <summary>Cheap per-file complexity-triage classification (replaces the size-based heuristic).</summary>
    ReviewTriage = 7,

    /// <summary>Evidence-gathering verification of a candidate finding before publication.</summary>
    ReviewVerification = 8,

    /// <summary>
    ///     Independent "second opinion" model for multi-pass union resample passes. When unbound the extra passes
    ///     are skipped (multi-pass degrades to a single pass) rather than falling back to the tier model.
    /// </summary>
    [JsonStringEnumMemberName("reviewUnionPass")]
    ReviewUnionPass = 9,
}
