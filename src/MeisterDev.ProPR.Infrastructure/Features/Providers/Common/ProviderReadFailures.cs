// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Preserves provider denial and throttle outcomes without provider response text.</summary>
internal static class ProviderReadFailures
{
    internal static void ThrowIfDeniedOrThrottled(HttpResponseMessage response, bool connectionWide = false)
    {
        if (ProviderThrottleSignal.IsThrottled(response))
        {
            throw new ProviderThrottledException();
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw Denial(response.StatusCode, connectionWide);
        }
    }

    internal static HttpRequestException Denial(HttpStatusCode status, bool connectionWide = false) =>
        new ProviderAccessDeniedException(status, connectionWide);

    internal static bool IsConnectionDenied(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProviderAccessDeniedException { ConnectionWide: true } ||
                current is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
            {
                return true;
            }
        }

        return false;
    }

    internal static HttpStatusCode? DeniedStatus(Exception exception)
    {
        if (ProviderThrottleSignal.IsThrottled(exception))
        {
            return null;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            var status = current switch
            {
                HttpRequestException http => http.StatusCode,
                _ => null,
            };
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return status;
            }
        }

        return null;
    }
}

/// <summary>Identifies a typed denial from connection verification or a repository operation.</summary>
internal sealed class ProviderAccessDeniedException(HttpStatusCode status, bool connectionWide)
    : HttpRequestException("The provider denied access to the requested repository data.", null, status)
{
    internal bool ConnectionWide { get; } = connectionWide || status == HttpStatusCode.Unauthorized;
}
