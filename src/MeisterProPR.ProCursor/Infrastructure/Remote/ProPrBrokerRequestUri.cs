// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.ProCursor.Options;
using Microsoft.Extensions.Options;

namespace MeisterProPR.ProCursor.Infrastructure.Remote;

internal static class ProPrBrokerRequestUri
{
    public static Uri Create(IOptions<ProCursorHostOptions> hostOptions, string path)
    {
        ArgumentNullException.ThrowIfNull(hostOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var baseUrl = hostOptions.Value.ProPrBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("PROCURSOR_PROPR_BASE_URL is required for ProCursor broker requests.");
        }

        return new Uri(new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute), path);
    }
}
