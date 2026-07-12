// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Configurable session-lifetime policy for browser refresh tokens. Sessions end when either
///     bound is crossed: the idle timeout (no <c>/auth/refresh</c> within the window) or the absolute
///     lifetime (measured from issuance, regardless of activity).
/// </summary>
public sealed record SessionPolicy
{
    /// <summary>
    ///     Maximum time a session may remain idle — with no successful refresh — before it expires.
    ///     Must comfortably exceed the access-token lifetime so an actively-used session is never
    ///     evicted mid-use.
    /// </summary>
    public required TimeSpan IdleTimeout { get; init; }

    /// <summary>
    ///     Maximum absolute lifetime of a session from issuance. A continuously-active session is still
    ///     forced to re-authenticate once this elapses.
    /// </summary>
    public required TimeSpan AbsoluteLifetime { get; init; }
}
