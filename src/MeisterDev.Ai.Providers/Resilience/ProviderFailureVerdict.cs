// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Resilience;

/// <summary>
///     What a driver concluded about one failed provider call: whether trying again could plausibly succeed, how
///     long to wait if the provider said so, and a short reason fit for an operator to read.
/// </summary>
/// <remarks>
///     This type exists so retry is decided by classification rather than by exception type. A provider SDK's
///     exception hierarchy is that SDK's business; whether the call is worth repeating is the driver's, and it is
///     the only party that can answer for its own transport.
/// </remarks>
/// <param name="IsTransient">Whether repeating the identical call could plausibly succeed.</param>
/// <param name="Reason">Short operator-facing description of what went wrong.</param>
/// <param name="RetryAfter">How long the provider asked the caller to wait, when it said so.</param>
/// <param name="HttpStatus">The HTTP status behind the failure, when the failure had one.</param>
public readonly record struct ProviderFailureVerdict(
    bool IsTransient,
    string Reason,
    TimeSpan? RetryAfter = null,
    int? HttpStatus = null)
{
    /// <summary>A failure that repeating cannot fix — a rejected request, a bad credential, a missing model.</summary>
    /// <param name="reason">Short operator-facing description of what went wrong.</param>
    /// <param name="httpStatus">The HTTP status behind the failure, when the failure had one.</param>
    public static ProviderFailureVerdict Permanent(string reason, int? httpStatus = null)
    {
        return new ProviderFailureVerdict(false, reason, null, httpStatus);
    }

    /// <summary>A failure worth repeating — throttling, a provider-side error, a dropped or timed-out connection.</summary>
    /// <param name="reason">Short operator-facing description of what went wrong.</param>
    /// <param name="retryAfter">How long the provider asked the caller to wait, when it said so.</param>
    /// <param name="httpStatus">The HTTP status behind the failure, when the failure had one.</param>
    public static ProviderFailureVerdict Transient(string reason, TimeSpan? retryAfter = null, int? httpStatus = null)
    {
        return new ProviderFailureVerdict(true, reason, retryAfter, httpStatus);
    }
}
