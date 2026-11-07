// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.Resilience;

/// <summary>
///     The two decisions every retrying wrapper has to make — is this failure ours to judge, and how long to wait
///     before trying again — and the form in which a wrapper reports a failure. They live here so the chat and
///     embedding wrappers cannot drift apart on any of them.
/// </summary>
internal static class ProviderRetryMechanics
{
    /// <summary>
    ///     Whether a failure may be classified and retried at all. Cancellation belongs to the caller, and a
    ///     refusal thrown on the cancellation channel — a budget hard cap, for instance — belongs to whoever
    ///     threw it, so both pass through untouched. The cancellations that are ours are the clients' own timeouts,
    ///     which arrive while the caller's token is healthy. The HTTP client's timeout wraps a
    ///     <see cref="TimeoutException" />. The network timeout of the System.ClientModel clients, which the OpenAI
    ///     clients use, fires when a response stream stops delivering data and wraps the interrupted read as an
    ///     <see cref="IOException" /> below a second cancellation.
    /// </summary>
    /// <param name="exception">The exception the call threw.</param>
    /// <param name="cancellationToken">The caller's token, used to tell a real cancellation from a timeout.</param>
    public static bool IsClassifiable(Exception exception, CancellationToken cancellationToken)
    {
        return exception is not OperationCanceledException
               || (!cancellationToken.IsCancellationRequested
                   && exception is TaskCanceledException
                   && CarriesTransportTimeout(exception.InnerException));
    }

    private static bool CarriesTransportTimeout(Exception? inner)
    {
        for (; inner is not null; inner = inner.InnerException)
        {
            if (inner is TimeoutException or IOException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Whether a classified failure earns another attempt. The rule lives here for the same reason the other two
    ///     do: the chat and embedding wrappers must not disagree about when a call has run out of attempts.
    /// </summary>
    /// <param name="policy">The retry policy in force.</param>
    /// <param name="verdict">The driver's classification of the failure.</param>
    /// <param name="attempt">The attempt that just failed, counted from one.</param>
    /// <param name="answerAlreadyStarted">
    ///     Whether part of an answer has already reached the caller. A streaming call that has handed over updates
    ///     cannot be retried, because a second attempt would either duplicate or contradict what was already read.
    /// </param>
    public static bool ShouldRetry(
        ProviderRetryPolicy policy,
        ProviderFailureVerdict verdict,
        int attempt,
        bool answerAlreadyStarted = false)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return !answerAlreadyStarted && verdict.IsTransient && attempt < policy.MaxAttempts;
    }

    /// <summary>
    ///     How long to wait before the next attempt: the provider's own stated delay when it gave one, otherwise
    ///     an exponential schedule. Both are capped by the policy — a <c>Retry-After</c> of an hour must not park
    ///     a review job for an hour.
    /// </summary>
    /// <param name="policy">The retry policy in force.</param>
    /// <param name="verdict">The classification, which may carry a provider-stated delay.</param>
    /// <param name="attempt">The attempt that just failed, counted from one.</param>
    public static TimeSpan Delay(ProviderRetryPolicy policy, ProviderFailureVerdict verdict, int attempt)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (verdict.RetryAfter is { } stated)
        {
            return stated < policy.MaxDelay ? stated : policy.MaxDelay;
        }

        var doubled = policy.BaseDelay * Math.Pow(2, attempt - 1);
        var capped = doubled < policy.MaxDelay ? doubled : policy.MaxDelay;
        if (policy.JitterFactor <= 0)
        {
            return capped;
        }

        var jittered = capped + (capped * policy.JitterFactor * Random.Shared.NextDouble());
        return jittered > policy.MaxDelay ? policy.MaxDelay : jittered;
    }

    /// <summary>
    ///     The verdict in the form a failure reports it: every credential value of the connection replaced in the
    ///     reason and in what the provider wrote, and what the provider wrote cut to
    ///     <see cref="DriverFailureMapper.MaximumProviderMessageLength" /> characters.
    /// </summary>
    /// <remarks>
    ///     A family decides what it puts on a verdict, and several providers quote the presented key back in a
    ///     refusal. The failure becomes the error of a pass and the reason a job failed, which operators read and
    ///     share, so the stage that reports it applies the host's scrub to both texts. The bound is applied again
    ///     here because a family loaded from a directory does not have to read the message the way the shipped
    ///     families do.
    /// </remarks>
    /// <param name="verdict">The driver's classification of the failure.</param>
    /// <param name="credentialValues">The credential values the connection holds.</param>
    public static ProviderFailureVerdict Reportable(ProviderFailureVerdict verdict, IReadOnlyCollection<string> credentialValues)
    {
        var reason = ProviderMessageGuard.Sanitize(verdict.Reason, credentialValues);
        var written = verdict.ProviderMessage is null
            ? string.Empty
            : ProviderMessageGuard.Sanitize(verdict.ProviderMessage, credentialValues, DriverFailureMapper.MaximumProviderMessageLength);

        return verdict with
        {
            Reason = reason.Length > 0 ? reason : verdict.Reason,
            ProviderMessage = written.Length > 0 ? written : null,
        };
    }
}
