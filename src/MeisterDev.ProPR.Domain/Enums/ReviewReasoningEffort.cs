// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     How much reasoning effort a review pass asks the model to spend. Configured per pass at the client level and
///     applied to the outbound chat request. <see cref="None" /> is the default. The remaining levels map to the
///     provider's low/medium/high reasoning-effort settings.
/// </summary>
public enum ReviewReasoningEffort
{
    /// <summary>
    ///     (Default) A model that accepts the reasoning effort none is sent none. Any other model receives no effort
    ///     level and uses its own default.
    /// </summary>
    None = 0,

    /// <summary>Low reasoning effort.</summary>
    Low = 1,

    /// <summary>Medium reasoning effort.</summary>
    Medium = 2,

    /// <summary>High reasoning effort.</summary>
    High = 3,
}
