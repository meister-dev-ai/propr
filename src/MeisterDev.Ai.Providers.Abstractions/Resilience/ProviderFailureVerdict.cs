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
/// <param name="IsThrottled">
///     Whether the provider refused the call for want of quota. Throttling is called out separately from the
///     other transient classes because it says something about the connection as a whole: every other caller
///     sharing it is about to be refused too. A stage that acts on that would otherwise have to re-derive it
///     from the HTTP status, which not every driver has.
/// </param>
public readonly record struct ProviderFailureVerdict(
    bool IsTransient,
    string Reason,
    TimeSpan? RetryAfter = null,
    int? HttpStatus = null,
    bool IsThrottled = false)
{
    // Held in fields so the pair is checked wherever either is set: the constructor and a `with` expression both
    // reach the property. Every positional member is declared below, in the order of the parameters, because a
    // record that declares only some of them emits the rest first.
    private readonly bool _isThrottled = IsThrottled;
    private readonly bool _isTransient = Consistent(IsTransient, IsThrottled);

    /// <summary>Whether repeating the identical call could plausibly succeed.</summary>
    public bool IsTransient
    {
        get => this._isTransient;
        init => this._isTransient = Consistent(value, this._isThrottled);
    }

    /// <summary>Whether the provider refused the call for want of quota.</summary>
    public bool IsThrottled
    {
        get => this._isThrottled;
        init => this._isThrottled = Consistent(this._isTransient, value) && value;
    }

    // Throttling is one of the transient classes, so a verdict cannot be throttled and permanent at once. A
    // stage reading the pair would hold back every other call bound for the connection and never repeat the one
    // that was refused, so the connection paces itself for a call nothing retries.
    private static bool Consistent(bool isTransient, bool isThrottled)
    {
        return isThrottled && !isTransient
            ? throw new ArgumentException(
                "A throttled failure is transient. A verdict that is throttled and not transient holds back every "
                + "other call on the connection and repeats none of them.",
                nameof(IsThrottled))
            : isTransient;
    }

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

    /// <summary>
    ///     A call the provider refused for want of quota. Transient like the rest, and additionally marked so a
    ///     later stage can hold back the other calls bound for the same connection.
    /// </summary>
    /// <param name="reason">Short operator-facing description of what went wrong.</param>
    /// <param name="retryAfter">How long the provider asked the caller to wait, when it said so.</param>
    /// <param name="httpStatus">The HTTP status behind the failure, when the failure had one.</param>
    public static ProviderFailureVerdict Throttled(string reason, TimeSpan? retryAfter = null, int? httpStatus = null)
    {
        return new ProviderFailureVerdict(true, reason, retryAfter, httpStatus, true);
    }
}
