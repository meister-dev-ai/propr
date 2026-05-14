// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Controls whether a strategy run is allowed to publish review findings.</summary>
public enum ReviewPublicationMode
{
    /// <summary>The selected strategy may publish according to existing final-gate rules.</summary>
    Publish,

    /// <summary>The strategy records results and must not publish provider comments.</summary>
    DryRun,

    /// <summary>The strategy records internal-only comparison results and must not publish provider comments.</summary>
    InternalOnly,
}
