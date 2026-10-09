// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Support;

/// <summary>Reads native pagination continuation from response headers.</summary>
internal static class ForgejoPaginationHeaders
{
    internal static int? ReadForgejoTotalCount(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!response.Headers.TryGetValues("X-Total-Count", out var values))
        {
            return null;
        }

        var totalCount = values.FirstOrDefault();

        return !string.IsNullOrWhiteSpace(totalCount)
               && int.TryParse(totalCount.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
               && total >= 0
            ? total
            : null;
    }
}
