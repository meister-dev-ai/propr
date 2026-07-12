// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;

namespace MeisterProPR.Application.Options;

/// <summary>
///     Per-account failed-login lockout policy. Validated on application startup. Overridable via the
///     <c>MEISTER_AUTH_LOCKOUT_*</c> environment variables.
/// </summary>
public sealed class AccountLockoutOptions
{
    /// <summary>Consecutive failed password attempts that trigger a lockout. Bound to <c>MEISTER_AUTH_LOCKOUT_MAX_ATTEMPTS</c>.</summary>
    [Range(1, 100, ErrorMessage = "MaxFailedAttempts must be between 1 and 100.")]
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>Lockout duration (minutes) applied at the first threshold. Bound to <c>MEISTER_AUTH_LOCKOUT_BASE_MINUTES</c>.</summary>
    [Range(1, 1440, ErrorMessage = "BaseLockoutMinutes must be between 1 and 1440.")]
    public int BaseLockoutMinutes { get; set; } = 15;

    /// <summary>Upper bound for the exponentially-backed-off lockout duration (minutes). Bound to <c>MEISTER_AUTH_LOCKOUT_MAX_MINUTES</c>.</summary>
    [Range(1, 10080, ErrorMessage = "MaxLockoutMinutes must be between 1 and 10080.")]
    public int MaxLockoutMinutes { get; set; } = 60;
}
