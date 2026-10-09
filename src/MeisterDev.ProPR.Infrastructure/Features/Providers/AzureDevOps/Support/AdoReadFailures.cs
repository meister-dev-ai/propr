// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Globalization;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;

/// <summary>Preserves provider denial and throttle outcomes without provider response text.</summary>
internal static class AdoReadFailures
{
    internal static void ThrowIfDeniedOrThrottled(HttpResponseMessage response, bool connectionWide = false)
    {
        if (IsThrottled(response))
        {
            throw new ProviderThrottledException();
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw Denial(response.StatusCode, connectionWide);
        }
    }

    internal static HttpRequestException Denial(HttpStatusCode status, bool connectionWide = false) =>
        ProviderReadFailures.Denial(status, connectionWide);

    internal static bool IsConnectionDenied(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProviderAccessDeniedException { ConnectionWide: true } ||
                current is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } ||
                current is VssServiceResponseException { HttpStatusCode: HttpStatusCode.Unauthorized } || current is VssUnauthorizedException)
            {
                return true;
            }
        }

        return false;
    }

    internal static HttpStatusCode? DeniedStatus(Exception exception)
    {
        if (IsThrottled(exception))
        {
            return null;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            var status = current switch
            {
                HttpRequestException http => http.StatusCode,
                VssServiceResponseException vss => vss.HttpStatusCode,
                VssUnauthorizedException => HttpStatusCode.Unauthorized,
                _ => null,
            };
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return status;
            }
        }

        return null;
    }

    internal static bool IsThrottled(HttpResponseMessage response)
    {
        if (ProviderThrottleSignal.IsThrottled(response))
        {
            return true;
        }

        if (response.StatusCode != HttpStatusCode.Forbidden || !response.Headers.TryGetValues("x-ratelimit-remaining", out var values))
        {
            return false;
        }

        var remaining = values.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(remaining) &&
               int.TryParse(remaining.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count <= 0;
    }

    internal static bool IsThrottled(Exception exception)
    {
        if (ProviderThrottleSignal.IsThrottled(exception))
        {
            return true;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is VssServiceResponseException { HttpStatusCode: HttpStatusCode.TooManyRequests })
            {
                return true;
            }
        }

        return false;
    }
}
