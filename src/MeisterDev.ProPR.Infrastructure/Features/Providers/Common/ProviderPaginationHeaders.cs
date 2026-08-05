// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Reads each provider's own answer to "is there another page" out of its response headers.
/// </summary>
/// <remarks>
///     Asking the provider beats counting items. A host is free to serve a smaller page than the request asked
///     for, and Forgejo does exactly that: it clamps a listing to its configured maximum response size, so a
///     loop that stopped as soon as a page came back smaller than requested would stop on the first page of
///     every collection on a host configured below the requested size. A null return means the provider said
///     nothing, which is the only case where page size is used to guess.
/// </remarks>
internal static class ProviderPaginationHeaders
{
    /// <summary>
    ///     GitHub advertises the next page as a <c>rel="next"</c> entry in its <c>Link</c> header, and omits
    ///     the entry on the last page.
    /// </summary>
    internal static bool? ReadGitHubHasMore(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        // Quoted is what GitHub itself sends; unquoted is accepted because the header is equally valid that way
        // and a proxy in front of an enterprise host may rewrite it.
        return values.Any(value => value.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)
                                   || value.Contains("rel=next", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     GitLab names the next page number in <c>X-Next-Page</c>, and sends the header empty on the last
    ///     page.
    /// </summary>
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

    /// <summary>
    ///     Forgejo reports how many items the whole collection holds in <c>X-Total-Count</c>, leaving the reader
    ///     to answer the question against how many it has read.
    /// </summary>
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
