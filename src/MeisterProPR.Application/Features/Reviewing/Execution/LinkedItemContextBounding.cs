// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution;

/// <summary>
///     Bounds the linked work items / issues before they are injected into the review context:
///     deduplicates by provider key (order-preserving), caps the item count, and truncates each
///     description. Keeps the eager context deterministic and within budget.
/// </summary>
internal static class LinkedItemContextBounding
{
    /// <summary>
    ///     Returns the bounded, deduplicated item list and reports how many items were dropped by the count cap.
    /// </summary>
    public static IReadOnlyList<LinkedItem> Bound(
        IReadOnlyList<LinkedItem> items,
        int maxItems,
        int maxDescriptionChars,
        out int droppedCount)
    {
        ArgumentNullException.ThrowIfNull(items);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<LinkedItem>();
        foreach (var item in items)
        {
            if (seen.Add(item.ProviderKey))
            {
                deduped.Add(item);
            }
        }

        var cap = Math.Max(0, maxItems);
        droppedCount = Math.Max(0, deduped.Count - cap);

        return deduped
            .Take(cap)
            .Select(item => TruncateDescription(item, maxDescriptionChars))
            .ToList()
            .AsReadOnly();
    }

    private static LinkedItem TruncateDescription(LinkedItem item, int maxChars)
    {
        if (maxChars <= 0 || item.Description is not { Length: > 0 } description || description.Length <= maxChars)
        {
            return item;
        }

        // Don't split a surrogate pair at the cut boundary (would leave a lone surrogate → U+FFFD).
        var cut = maxChars;
        if (char.IsHighSurrogate(description[cut - 1]))
        {
            cut--;
        }

        return item with { Description = string.Concat(description.AsSpan(0, cut), "…") };
    }
}
