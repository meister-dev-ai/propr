// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Reads bounded page-numbered REST collections and rejects repeated or incomplete reads.</summary>
internal static class ProviderRestPager
{
    /// <summary>Sets the requested REST page size.</summary>
    internal const int PageSize = 100;

    /// <summary>Bounds collection reads when a provider ignores pagination.</summary>
    internal const int MaxPages = 30;

    /// <summary>Contains collection items and normalized continuation or total-count information.</summary>
    internal readonly record struct RestPage<T>(
        IReadOnlyList<T> Items,
        bool? HasMore = null,
        int? TotalCount = null);

    /// <param name="loadPageAsync">Reads a one-based page using the requested page size.</param>
    /// <param name="identify">Returns the stable item identity used for duplicate detection.</param>
    /// <param name="collectionDescription">Identifies the collection in operator-visible failure messages.</param>
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

            // A fully repeated page indicates that pagination did not advance; partial overlap is deduplicated.
            if (added == 0)
            {
                throw new InvalidOperationException(ProviderPaginationFailure.RepeatedPage(collectionDescription));
            }

            // Compare provider totals with retained items because providers may return fewer items than requested.
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

            // A short page completes the read only when the provider supplies no continuation information.
            if (current.HasMore is null && current.Items.Count < pageSize)
            {
                return items.AsReadOnly();
            }
        }

        throw new InvalidOperationException(ProviderPaginationFailure.ExceededLimit(collectionDescription, items.Count));
    }

    /// <summary>Returns null when an optional comparison collection cannot be completed.</summary>
    /// <remarks>Callers can then review every changed file without reducing review coverage.</remarks>
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
        catch (InvalidOperationException exception) when (exception is not ProviderThrottledException)
        {
            return null;
        }
    }
}
