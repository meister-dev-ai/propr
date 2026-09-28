// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.RateLimiting;

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;

/// <summary>Bounds machine credential verification work before BCrypt runs.</summary>
public sealed class TenantMachineAuthenticationThrottle : IDisposable
{
    public const string ThrottledItemKey = "TenantMachineAuthenticationThrottled";
    private const int MaxTrackedCredentials = 4096;
    private readonly FixedWindowRateLimiter _globalLimiter;
    private readonly Dictionary<Guid, CredentialWindow> _credentialWindows = [];
    private readonly Lock _gate = new();
    private readonly int _perCredentialPermits;
    private readonly TimeSpan _window;

    public TenantMachineAuthenticationThrottle()
        : this(256, 16, TimeSpan.FromSeconds(1))
    {
    }

    public TenantMachineAuthenticationThrottle(int globalPermits, int perCredentialPermits, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(globalPermits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perCredentialPermits);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        this._perCredentialPermits = perCredentialPermits;
        this._window = window;
        this._globalLimiter = new FixedWindowRateLimiter(
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = globalPermits,
                Window = window,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    }

    public bool TryAcquireGlobal()
    {
        using var lease = this._globalLimiter.AttemptAcquire();
        return lease.IsAcquired;
    }

    public bool TryAcquireCredential(Guid credentialId)
    {
        lock (this._gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (this._credentialWindows.TryGetValue(credentialId, out var current) &&
                now - current.Start < this._window)
            {
                if (current.Count >= this._perCredentialPermits)
                {
                    return false;
                }

                this._credentialWindows[credentialId] = current with { Count = current.Count + 1 };
                return true;
            }

            if (this._credentialWindows.Count >= MaxTrackedCredentials)
            {
                foreach (var expired in this._credentialWindows
                             .Where(x => now - x.Value.Start >= this._window)
                             .Select(x => x.Key).ToArray())
                {
                    this._credentialWindows.Remove(expired);
                }

                if (this._credentialWindows.Count >= MaxTrackedCredentials)
                {
                    return false;
                }
            }

            this._credentialWindows[credentialId] = new CredentialWindow(now, 1);
            return true;
        }
    }

    public void Dispose() => this._globalLimiter.Dispose();

    private sealed record CredentialWindow(DateTimeOffset Start, int Count);
}

public sealed class TenantMachineCredentialThrottledException : Exception;
