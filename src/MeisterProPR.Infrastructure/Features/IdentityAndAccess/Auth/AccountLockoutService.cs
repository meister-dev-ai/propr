// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Auth;

/// <summary>
///     Postgres-backed per-account lockout: the failure count and lockout expiry live on the user row so the
///     policy holds across replicas. A lockout applies once consecutive failures reach the configured threshold,
///     with the duration doubling each further threshold block up to a cap.
/// </summary>
public sealed class AccountLockoutService(IUserRepository userRepository, IOptions<AccountLockoutOptions> options)
    : IAccountLockoutService
{
    private readonly AccountLockoutOptions _options = options.Value;

    /// <inheritdoc />
    public bool IsLockedOut(AppUser user)
    {
        return user.LockoutEndAt is { } lockoutEnd && lockoutEnd > DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public async Task RecordFailureAsync(AppUser user, CancellationToken ct = default)
    {
        var attempts = await userRepository.IncrementFailedLoginAttemptsAsync(user.Id, ct).ConfigureAwait(false);
        user.FailedLoginAttempts = attempts;

        if (attempts >= this._options.MaxFailedAttempts)
        {
            var thresholdBlocks = attempts / this._options.MaxFailedAttempts;
            var minutes = Math.Min(
                this._options.BaseLockoutMinutes * Math.Pow(2, thresholdBlocks - 1),
                this._options.MaxLockoutMinutes);
            user.LockoutEndAt = DateTimeOffset.UtcNow.AddMinutes(minutes);
            await userRepository.SetLockoutEndAsync(user.Id, user.LockoutEndAt, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ResetAsync(AppUser user, CancellationToken ct = default)
    {
        if (user.FailedLoginAttempts == 0 && user.LockoutEndAt is null)
        {
            return;
        }

        user.FailedLoginAttempts = 0;
        user.LockoutEndAt = null;
        await userRepository.ResetFailedLoginsAsync(user.Id, ct).ConfigureAwait(false);
    }
}
