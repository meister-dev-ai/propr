// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Enforces the per-account failed-login lockout for the local password sign-in paths: it throttles online
///     password guessing against a single account independently of the per-IP rate limit.
/// </summary>
public interface IAccountLockoutService
{
    /// <summary>Returns <c>true</c> when the user is currently locked out (a lockout is set and has not yet elapsed).</summary>
    bool IsLockedOut(AppUser user);

    /// <summary>
    ///     Records a failed password attempt for the user, applying an exponentially-backed-off lockout once the
    ///     configured consecutive-failure threshold is reached.
    /// </summary>
    Task RecordFailureAsync(AppUser user, CancellationToken ct = default);

    /// <summary>Clears the failure count and lockout after a successful sign-in (a no-op when already clear).</summary>
    Task ResetAsync(AppUser user, CancellationToken ct = default);
}
