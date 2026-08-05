// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Reads every page of a page-numbered REST collection and returns their union.
/// </summary>
/// <remarks>
///     Every way out of the loop is either the provider saying that was the last page, or an exception. A
///     collection read to its first page only would drop files out of the review's scope and threads out of its
///     conversation while the run still reported success, which is the failure this exists to prevent.
/// </remarks>
internal static class ProviderRestPager
{
    /// <summary>
    ///     What each provider's listing endpoints accept as their largest page.
    /// </summary>
    internal const int PageSize = 100;

    /// <summary>
    ///     How many pages one collection is read across. Generous enough to be unreachable on a pull request
    ///     anyone would open: at the full page size it is more changed files than GitHub's own API will serve
    ///     for a single pull request. It exists so that a host which ignores the page parameter is a bounded
    ///     failure rather than a loop that never ends.
    /// </summary>
    internal const int MaxPages = 30;

    /// <summary>
    ///     A page of items together with whatever the provider said about the rest of the collection: either a
    ///     direct answer in <paramref name="HasMore" />, or the collection's size in
    ///     <paramref name="TotalCount" />, which is answered against how much has been read. When the provider
    ///     said neither, the page's own size is all there is to go on.
    /// </summary>
    internal readonly record struct RestPage<T>(
        IReadOnlyList<T> Items,
        bool? HasMore = null,
        int? TotalCount = null);

    /// <param name="loadPageAsync">Reads one page, given its one-based page number and the page size to ask for.</param>
    /// <param name="identify">
    ///     Names an item uniquely within the collection. Used both to return each item once and to notice a host
    ///     that answers every page with the same one.
    /// </param>
    /// <param name="collectionDescription">
    ///     How the collection is named in a failure, e.g. "GitHub's changed-file listing for pull request 42".
    ///     It reaches the operator as the review's error message.
    /// </param>
    internal static async Task<IReadOnlyList<T>> LoadAllAsync<T>(
        Func<int, int, CancellationToken, Task<RestPage<T>>> loadPageAsync,
        Func<T, string> identify,
        string collectionDescription,
        CancellationToken cancellationToken,
        int pageSize = PageSize,
        int maxPages = MaxPages)
    {
        ArgumentNullException.ThrowIfNull(loadPageAsync);
        ArgumentNullException.ThrowIfNull(identify);

        var items = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 1; page <= maxPages; page++)
        {
            var current = await loadPageAsync(page, pageSize, cancellationToken);
            if (current.Items.Count == 0)
            {
                return items.AsReadOnly();
            }

            var added = 0;
            foreach (var item in current.Items)
            {
                if (seen.Add(identify(item)))
                {
                    items.Add(item);
                    added++;
                }
            }

            // Nothing new from a non-empty page means the host served a page it had already served. Ordering
            // can shift between requests and repeat an item or two legitimately, so only a page that repeats
            // in full is treated as the host ignoring the page number.
            if (added == 0)
            {
                throw new InvalidOperationException(ProviderPaginationFailure.RepeatedPage(collectionDescription));
            }

            // A total count is answered against what has actually been read, which is why the pager owns the
            // running count: a host serving smaller pages than were asked for makes any count derived from the
            // requested page size too high, and too high here reads as "that was all of it".
            if (current.TotalCount is { } totalCount)
            {
                if (items.Count >= totalCount)
                {
                    return items.AsReadOnly();
                }

                continue;
            }

            if (current.HasMore == false)
            {
                return items.AsReadOnly();
            }

            // Only where the provider said nothing at all does a page smaller than the one asked for end the
            // read. On a host that both serves short pages and reports no total this is still a guess, and the
            // only alternative would be an extra request per collection to prove every read complete.
            if (current.HasMore is null && current.Items.Count < pageSize)
            {
                return items.AsReadOnly();
            }
        }

        throw new InvalidOperationException(ProviderPaginationFailure.ExceededLimit(collectionDescription, items.Count));
    }

    /// <summary>
    ///     As <see cref="LoadAllAsync{T}" />, but answers null where that read could not be completed.
    /// </summary>
    /// <remarks>
    ///     Only for a collection whose absence widens what gets reviewed rather than narrowing it. The
    ///     comparison behind a delta review is the one such collection: without it the review falls back to
    ///     every changed file, which costs more than it needs to and leaves nothing out. Failing loudly is for
    ///     the reads whose absence would shrink the review.
    /// </remarks>
    internal static async Task<IReadOnlyList<T>?> TryLoadAllAsync<T>(
        Func<int, int, CancellationToken, Task<RestPage<T>>> loadPageAsync,
        Func<T, string> identify,
        string collectionDescription,
        CancellationToken cancellationToken,
        int pageSize = PageSize,
        int maxPages = MaxPages)
    {
        try
        {
            return await LoadAllAsync(
                loadPageAsync,
                identify,
                collectionDescription,
                cancellationToken,
                pageSize,
                maxPages);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
