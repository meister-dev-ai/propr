// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Reads every page of a cursor-paginated GraphQL connection and returns their union.
/// </summary>
/// <remarks>
///     The counterpart to <see cref="ProviderRestPager" /> for GitHub's review threads, which exist only in
///     GraphQL. A connection carries its own answer in <c>pageInfo</c>, so there is no page size to guess from:
///     the read ends when the connection says there is no next page, and otherwise resumes from its cursor.
/// </remarks>
internal static class ProviderCursorPager
{
    /// <summary>
    ///     The largest page a GitHub GraphQL connection accepts.
    /// </summary>
    internal const int PageSize = 100;

    /// <summary>
    ///     How many pages one connection is read across, on the same reasoning as
    ///     <see cref="ProviderRestPager.MaxPages" />: unreachable in practice, and there so that a connection
    ///     which claims a next page forever is a bounded failure rather than an endless loop.
    /// </summary>
    internal const int MaxPages = 30;

    /// <summary>
    ///     One page of a connection, as its <c>nodes</c> and its <c>pageInfo</c>.
    /// </summary>
    internal readonly record struct CursorPage<T>(IReadOnlyList<T> Nodes, bool HasNextPage, string? EndCursor);

    /// <param name="loadPageAsync">
    ///     Reads one page, given the cursor to resume after. Null asks for the first page.
    /// </param>
    /// <param name="collectionDescription">
    ///     How the connection is named in a failure. It reaches the operator as the review's error message.
    /// </param>
    internal static async Task<IReadOnlyList<T>> LoadAllAsync<T>(
        Func<string?, CancellationToken, Task<CursorPage<T>>> loadPageAsync,
        string collectionDescription,
        CancellationToken cancellationToken,
        int maxPages = MaxPages)
    {
        ArgumentNullException.ThrowIfNull(loadPageAsync);

        var nodes = new List<T>();
        string? cursor = null;

        for (var page = 1; page <= maxPages; page++)
        {
            var current = await loadPageAsync(cursor, cancellationToken);
            nodes.AddRange(current.Nodes);

            if (!current.HasNextPage)
            {
                return nodes.AsReadOnly();
            }

            // A next page with nowhere to resume from, or with the cursor this page was already read at, has
            // no continuation: asking again would re-read the same page until the bound.
            if (string.IsNullOrWhiteSpace(current.EndCursor)
                || string.Equals(current.EndCursor, cursor, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ProviderPaginationFailure.RepeatedPage(collectionDescription));
            }

            cursor = current.EndCursor;
        }

        throw new InvalidOperationException(ProviderPaginationFailure.ExceededLimit(collectionDescription, nodes.Count));
    }
}
