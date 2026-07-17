// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     How much reasoning effort a review pass asks the model to spend. Configured per pass at the client level and
///     applied to the outbound chat request. <see cref="None" /> is the default and leaves the request without an
///     effort level, so the provider keeps its own default (no reasoning) — behavior and cost stay unchanged until a
///     user opts in. The remaining levels map to the provider's low/medium/high reasoning-effort settings.
/// </summary>
public enum ReviewReasoningEffort
{
    /// <summary>
    ///     (Default) No effort level is set on the request, so the provider keeps its own default — the reasoning
    ///     models on the deployments in use then perform no reasoning. Behavior and cost stay unchanged.
    /// </summary>
    None = 0,

    /// <summary>Low reasoning effort.</summary>
    Low = 1,

    /// <summary>Medium reasoning effort.</summary>
    Medium = 2,

    /// <summary>High reasoning effort.</summary>
    High = 3,
}
