// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Support;

/// <summary>Reads native pagination continuation from response headers.</summary>
internal static class GitHubPaginationHeaders
{
    internal static bool? ReadGitHubHasMore(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        // Link relations may use quoted or unquoted values.
        return values.Any(value => value.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)
                                   || value.Contains("rel=next", StringComparison.OrdinalIgnoreCase));
    }
}
