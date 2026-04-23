// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Origin of a configured model entry.
/// </summary>
public enum AiConfiguredModelSource
{
    /// <summary>Discovered directly from the provider.</summary>
    Discovered = 0,

    /// <summary>Entered manually by an operator.</summary>
    Manual = 1,

    /// <summary>Filled from a built-in known catalog.</summary>
    KnownCatalog = 2,
}
