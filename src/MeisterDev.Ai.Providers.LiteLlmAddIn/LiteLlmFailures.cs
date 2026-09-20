// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.Globalization;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Resilience;

namespace MeisterDev.Ai.Providers.LiteLlmAddIn;

/// <summary>
///     The failure shape the OpenAI client library produces, mapped onto the same verdicts
///     <see cref="DriverFailureMapper" /> produces for everything else.
/// </summary>
/// <remarks>
///     <para>
///         <c>ClientResultException</c> comes from System.ClientModel, which arrives with that library. The
///         contract assembly takes no vendor dependency, so recognising the type belongs with the family that
///         carries the library.
///     </para>
///     <para>
///         It has to be recognised in the assembly that references the library for a second reason: this family
///         resolves System.ClientModel from its own folder, so an exception it raises is of a type the host's own
///         copy does not match. A classification made anywhere else would read it as an unrecognised failure and
///         call a rate limit permanent.
///     </para>
/// </remarks>
internal static class LiteLlmFailures
{
    /// <summary>
    ///     Classifies a runtime failure, deferring to <see cref="DriverFailureMapper.ClassifyRuntimeFailure" />
    ///     for anything the client library did not raise.
    /// </summary>
    /// <param name="exception">The exception the call threw.</param>
    internal static ProviderFailureVerdict Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (var candidate in DriverFailureMapper.Unwind(exception))
        {
            if (candidate is ClientResultException refused)
            {
                return DriverFailureMapper.ClassifyStatus(refused.Status, StatedWait(refused));
            }
        }

        return DriverFailureMapper.ClassifyRuntimeFailure(exception);
    }

    /// <summary>
    ///     The wait to honour for one failure, drawn from the header when it names a real one and from the
    ///     response body otherwise. A <c>Retry-After</c> of zero, or an HTTP date that has already passed, is a
    ///     header that grants no wait at all; letting it win over a body that states seconds would send the whole
    ///     fan-out straight back into the exhausted quota. With nothing in the body, the header still decides,
    ///     zero included.
    /// </summary>
    /// <param name="exception">The failure, carrying both the raw response and the body in its message.</param>
    private static TimeSpan? StatedWait(ClientResultException exception)
    {
        var header = RetryAfter(exception);
        return header > TimeSpan.Zero ? header : DriverFailureMapper.ReadStatedDelay(exception.Message) ?? header;
    }

    // Providers state their own backoff on 429 and sometimes on 503. Honouring it beats guessing, so it is read
    // off the raw response rather than left to the exponential schedule.
    private static TimeSpan? RetryAfter(ClientResultException exception)
    {
        var response = exception.GetRawResponse();
        if (response is null
            || !response.Headers.TryGetValue("Retry-After", out var value)
            || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out var seconds) && seconds >= 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        // The header also allows an HTTP date. A date already in the past yields no wait rather than a negative one.
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until))
        {
            return null;
        }

        var remaining = until - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
