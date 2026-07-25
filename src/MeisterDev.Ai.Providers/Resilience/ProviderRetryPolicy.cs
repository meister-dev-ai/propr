// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Resilience;

/// <summary>
///     How hard to try again after a transient provider failure. Supplied by the host, because how long a caller
///     is willing to wait is a product decision rather than a provider fact.
/// </summary>
public sealed record ProviderRetryPolicy
{
    /// <summary>Modest defaults: three attempts, one second of base backoff, half a minute of ceiling.</summary>
    public static ProviderRetryPolicy Default { get; } = new();

    /// <summary>
    ///     Total attempts including the first, so <c>1</c> disables retrying. Counting attempts rather than
    ///     retries is deliberate: "how many times did we call the provider" is the number that shows up in
    ///     spend and in logs.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Backoff before the second attempt; each further attempt doubles it.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling for a single backoff, which the doubling and any provider-stated delay both respect.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Fraction of the computed backoff to spread randomly, as a defence against many jobs that were
    ///     throttled together marching back in step. Zero makes the backoff exactly reproducible, which is what
    ///     tests want.
    /// </summary>
    public double JitterFactor { get; init; } = 0.2;
}
