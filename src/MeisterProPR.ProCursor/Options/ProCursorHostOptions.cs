// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;

namespace MeisterProPR.ProCursor.Options;

/// <summary>
///     Host-level configuration for the extracted ProCursor service.
/// </summary>
public sealed class ProCursorHostOptions
{
    /// <summary>
    ///     Internal base URL for the ProPR control plane and broker endpoints.
    /// </summary>
    [Url]
    public string? ProPrBaseUrl { get; set; }

    /// <summary>
    ///     Shared symmetric key used for the service boundary.
    /// </summary>
    public string? SharedKey { get; set; }

    /// <summary>
    ///     Timeout budget for ProCursor -> ProPR broker calls.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    ///     Freshness window for in-memory runtime configuration cache entries.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RuntimeConfigurationTtlSeconds { get; set; } = 300;

}
