// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;

namespace MeisterProPR.Application.Options;

/// <summary>
///     Per-client-IP rate-limit policy for the authentication endpoints. Validated on application startup.
///     Overridable via the <c>MEISTER_AUTH_RATELIMIT_*</c> environment variables. Disabled by default under
///     the <c>Testing</c> environment (the in-memory test server has no per-request client IP).
/// </summary>
public sealed class AuthRateLimitOptions
{
    /// <summary>Whether the auth rate limiter is active. Bound to <c>MEISTER_AUTH_RATELIMIT_ENABLED</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Requests permitted per window per client IP. This is coarse flood control (co-located users share an
    ///     egress IP), so it is deliberately loose; the tight per-account brute-force control is the account
    ///     lockout. Bound to <c>MEISTER_AUTH_RATELIMIT_PERMITS</c>.
    /// </summary>
    [Range(1, 10000, ErrorMessage = "PermitLimit must be between 1 and 10000.")]
    public int PermitLimit { get; set; } = 20;

    /// <summary>Fixed-window length in seconds. Bound to <c>MEISTER_AUTH_RATELIMIT_WINDOW_SECONDS</c>.</summary>
    [Range(1, 3600, ErrorMessage = "WindowSeconds must be between 1 and 3600.")]
    public int WindowSeconds { get; set; } = 60;
}
