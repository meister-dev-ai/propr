// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Recognizes standard HTTP throttling responses and normalized failures.</summary>
internal static class ProviderThrottleSignal
{
    /// <summary>Checks standard HTTP status and retry headers.</summary>
    internal static bool IsThrottled(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return response.Headers.RetryAfter is not null;
        }

        return response.StatusCode == HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is not null;
    }

    /// <summary>Checks normalized throttling failures throughout the exception chain.</summary>
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
}
