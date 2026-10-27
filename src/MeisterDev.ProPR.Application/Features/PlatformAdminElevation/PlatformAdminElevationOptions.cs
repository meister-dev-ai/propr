// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.ComponentModel.DataAnnotations;

namespace MeisterDev.ProPR.Application.Features.PlatformAdminElevation;

/// <summary>
///     The installation's bound on elevations. Validated on application startup, so an installation with a value
///     outside the accepted range does not start.
/// </summary>
public sealed class PlatformAdminElevationOptions
{
    /// <summary>The environment variable that sets <see cref="MaxDurationMinutes" />.</summary>
    public const string MaxDurationMinutesKey = "MEISTER_PLATFORM_ADMIN_ELEVATION_MAX_MINUTES";

    /// <summary>The longest elevation when the installation sets no value: four hours.</summary>
    public const int DefaultMaxDurationMinutes = 240;

    /// <summary>The longest duration, in minutes, that a platform administrator can request for one elevation.</summary>
    [Range(1, 1440, ErrorMessage = "MEISTER_PLATFORM_ADMIN_ELEVATION_MAX_MINUTES must be between 1 and 1440.")]
    public int MaxDurationMinutes { get; set; } = DefaultMaxDurationMinutes;
}
