// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Support;

/// <summary>Reads native pagination continuation from response headers.</summary>
internal static class GitLabPaginationHeaders
{
    internal static bool? ReadGitLabHasMore(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!response.Headers.TryGetValues("X-Next-Page", out var values))
        {
            return null;
        }

        var nextPage = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(nextPage))
        {
            return false;
        }

        return int.TryParse(nextPage.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var page)
               && page > 0;
    }
}
