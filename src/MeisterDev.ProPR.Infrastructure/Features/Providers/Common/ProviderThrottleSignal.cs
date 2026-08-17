// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Recognises a provider saying "too fast" rather than "no".
/// </summary>
/// <remarks>
///     A throttle is a temporary answer, so a caller that treats it as a failure spends its retry budget on a
///     limit that will clear on its own. The mention scan uses this to stop asking for the rest of a tick and
///     resume on the next one.
/// </remarks>
internal static class ProviderThrottleSignal
{
    /// <summary>Reports whether a response is the provider asking the caller to slow down.</summary>
    internal static bool IsThrottled(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        // GitHub reports a secondary rate limit as 403 with a Retry-After, and an exhausted primary limit as
        // 403 with the remaining count at zero. Neither is an authorization answer, and treating them as one
        // would have an operator checking a token that is fine.
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return response.Headers.RetryAfter is not null || HasExhaustedRateLimit(response);
        }

        // A gateway that names a wait is pacing the caller. One that does not is an outage, which is a
        // different thing and is left to fail.
        return response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is not null;
    }

    /// <summary>Reports whether a failure carries a provider's throttling answer anywhere in its chain.</summary>
    internal static bool IsThrottled(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProviderThrottledException
                or HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasExhaustedRateLimit(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-ratelimit-remaining", out var values))
        {
            return false;
        }

        var remaining = values.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(remaining)
               && int.TryParse(remaining.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
               && count <= 0;
    }
}
