// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using Google.Apis.Auth.OAuth2.Responses;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Resilience;

namespace MeisterDev.Ai.Providers.GoogleVertexAddIn;

/// <summary>
///     Classifies what a Vertex call throws, including what Google's token service raises.
/// </summary>
/// <remarks>
///     A Vertex call mints a token before it reaches the model, so a runtime failure can come from the token
///     service rather than from the provider. Those arrive as <see cref="TokenResponseException" />, which the
///     shared classifier does not recognise, so it read every one of them as permanent. A token service that is
///     briefly unavailable then took the call out with it and nothing retried.
/// </remarks>
internal static class GoogleFailures
{
    /// <summary>
    ///     Classifies a runtime failure, deferring to <see cref="DriverFailureMapper.ClassifyRuntimeFailure" />
    ///     for anything the token service did not raise.
    /// </summary>
    /// <param name="exception">The exception the call threw.</param>
    internal static ProviderFailureVerdict Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (var candidate in DriverFailureMapper.Unwind(exception))
        {
            if (candidate is TokenResponseException refused)
            {
                return FromTokenService(refused);
            }
        }

        return DriverFailureMapper.ClassifyRuntimeFailure(exception);
    }

    // The status where the exception carries one, and the OAuth error code otherwise. A token service answers a
    // refusal with 400 and an outage with 5xx, and the code says which of the two a bare exception is: a
    // credential the service rejected is permanent, and one it could not decide about right now is not.
    private static ProviderFailureVerdict FromTokenService(TokenResponseException refused)
    {
        var reason = Reason(refused);

        if (refused.StatusCode is { } status)
        {
            return DriverFailureMapper.ClassifyStatus((int)status, null) with { Reason = reason };
        }

        return refused.Error?.Error switch
        {
            "temporarily_unavailable" or "slow_down" => ProviderFailureVerdict.Transient(reason),
            _ => ProviderFailureVerdict.Permanent(reason),
        };
    }

    // Google's own wording, which names the credential problem better than anything this driver could write,
    // prefixed so an operator reading it knows the refusal came from the token service and not from Vertex.
    private static string Reason(TokenResponseException refused)
    {
        var stated = refused.Error?.ErrorDescription ?? refused.Error?.Error ?? refused.Message;

        return $"Google's token service refused the credential: {stated}";
    }
}
