// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Resilience;

/// <summary>
///     A provider call that could not be completed, reported in terms of the configuration that produced it.
/// </summary>
/// <remarks>
///     A raw SDK exception says "Service request failed. Status: 401". That is true and useless: it names neither
///     the profile to go and fix nor what to do about it, and it is what ends up written on a failed job as its
///     cause. This exception carries the profile, the model, how many attempts were spent and what to try next,
///     so a job's recorded failure reason is actionable. The original exception is kept as the inner one, so no
///     provider detail is lost.
/// </remarks>
public sealed class ProviderCallFailedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ProviderCallFailedException" /> class.</summary>
    /// <param name="target">The profile, provider and model the call was made against.</param>
    /// <param name="verdict">The classification the driver returned for the failure.</param>
    /// <param name="attempts">How many attempts were made in total, including the first.</param>
    /// <param name="actionHint">What an operator should try next; omitted when nothing specific can be said.</param>
    /// <param name="innerException">The provider exception that ended the call.</param>
    public ProviderCallFailedException(
        ProviderCallTarget target,
        ProviderFailureVerdict verdict,
        int attempts,
        string? actionHint,
        Exception? innerException = null)
        : base(BuildMessage(target, verdict, attempts, actionHint), innerException)
    {
        ArgumentNullException.ThrowIfNull(target);

        this.Target = target;
        this.Verdict = verdict;
        this.Attempts = attempts;
        this.ActionHint = actionHint;
    }

    /// <summary>The profile, provider and model the call was made against.</summary>
    public ProviderCallTarget Target { get; }

    /// <summary>The classification the driver returned for the failure.</summary>
    public ProviderFailureVerdict Verdict { get; }

    /// <summary>How many attempts were made in total, including the first.</summary>
    public int Attempts { get; }

    /// <summary>What an operator should try next, when something specific can be said.</summary>
    public string? ActionHint { get; }

    /// <summary>The provider family the failed call was routed to.</summary>
    public AiProviderKind ProviderKind => this.Target.ProviderKind;

    private static string BuildMessage(
        ProviderCallTarget target,
        ProviderFailureVerdict verdict,
        int attempts,
        string? actionHint)
    {
        ArgumentNullException.ThrowIfNull(target);

        var attemptClause = attempts <= 1 ? "on the first attempt" : $"after {attempts} attempts";
        var message = $"The AI provider call failed {attemptClause} ({target.Describe()}): {verdict.Reason}";
        return actionHint is { Length: > 0 } hint ? $"{message} {hint}" : message;
    }
}
