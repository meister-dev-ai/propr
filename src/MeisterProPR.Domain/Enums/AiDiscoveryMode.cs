// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Controls whether operators rely on provider discovery or manual model entry.
/// </summary>
public enum AiDiscoveryMode
{
    /// <summary>Use provider catalog discovery when available.</summary>
    ProviderCatalog = 0,

    /// <summary>Skip discovery and require manual model entry.</summary>
    ManualOnly = 1,
}
