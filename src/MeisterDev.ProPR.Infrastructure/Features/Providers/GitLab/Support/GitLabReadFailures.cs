// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Globalization;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Support;

/// <summary>Normalizes native rate-limit responses before shared denial classification.</summary>
internal static class GitLabReadFailures
{
    internal static void ThrowIfDeniedOrThrottled(HttpResponseMessage response, bool connectionWide = false)
    {
        if (IsThrottled(response))
        {
            throw new ProviderThrottledException();
        }

        ProviderReadFailures.ThrowIfDeniedOrThrottled(response, connectionWide);
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
}
