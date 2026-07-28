// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Resilience;

/// <summary>
///     The two decisions every retrying wrapper has to make — is this failure ours to judge, and how long to wait
///     before trying again. They live here so the chat and embedding wrappers cannot drift apart on either one.
/// </summary>
internal static class ProviderRetryMechanics
{
    /// <summary>
    ///     Whether a failure may be classified and retried at all. Cancellation belongs to the caller, and a
    ///     refusal thrown on the cancellation channel — a budget hard cap, for instance — belongs to whoever
    ///     threw it, so both pass through untouched. The one cancellation that is ours is the HTTP client's own
    ///     timeout: it wraps a <see cref="TimeoutException" /> and arrives while the caller's token is healthy.
    /// </summary>
    /// <param name="exception">The exception the call threw.</param>
    /// <param name="cancellationToken">The caller's token, used to tell a real cancellation from a timeout.</param>
    public static bool IsClassifiable(Exception exception, CancellationToken cancellationToken)
    {
        return exception is not OperationCanceledException
               || (!cancellationToken.IsCancellationRequested
                   && exception is TaskCanceledException { InnerException: TimeoutException });
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
        ArgumentNullException.ThrowIfNull(verdict);

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
}
