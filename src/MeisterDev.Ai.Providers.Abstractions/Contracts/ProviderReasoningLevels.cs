// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     Reasoning levels by the names providers give them, for example <c>none</c>, <c>low</c> or <c>max</c>, and the
///     order of the names that several providers share.
/// </summary>
/// <remarks>
///     Each provider names its own levels, so a level is a name and not an enum value. The names in
///     <see cref="StandardOrder" /> are the ones that OpenAI, Anthropic, Google and the gateways in front of them use,
///     from the lowest to the highest effort. A list made only of these names is sorted by that order. A list with any
///     other name keeps the order of its source, because the position of an unknown name cannot be inferred.
/// </remarks>
public static class ProviderReasoningLevels
{
    /// <summary>The longest level name that a list holds.</summary>
    public const int MaxNameLength = 32;

    /// <summary>The largest number of levels that one model lists.</summary>
    public const int MaxCount = 16;

    /// <summary>The shared level names, from the lowest to the highest effort.</summary>
    /// <remarks>An immutable array, so that no caller can change the order every other caller reads.</remarks>
    public static IReadOnlyList<string> StandardOrder { get; } = ImmutableArray.Create("none", "minimal", "low", "medium", "high", "xhigh", "max");

    /// <summary>
    ///     Whether <paramref name="name" /> is a level name: 1 to <see cref="MaxNameLength" /> characters of lowercase
    ///     ASCII letters, digits, hyphens and underscores, starting with a letter.
    /// </summary>
    /// <param name="name">The name to check, already trimmed and lowercased.</param>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength || name[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var character in name)
        {
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The position of <paramref name="name" /> in <see cref="StandardOrder" />, or -1 for any other name.</summary>
    /// <param name="name">The level name.</param>
    public static int RankOf(string? name)
    {
        for (var index = 0; index < StandardOrder.Count; index++)
        {
            if (string.Equals(StandardOrder[index], name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    ///     Normalizes a list of level names: trims and lowercases each name, drops names that are not level names and
    ///     repeated names, keeps at most <see cref="MaxCount" /> names, and sorts the list by
    ///     <see cref="StandardOrder" /> when every name is on that order.
    /// </summary>
    /// <param name="levels">The names as a source states them, or null.</param>
    /// <returns>The normalized list, which can be empty, or null when <paramref name="levels" /> is null.</returns>
    public static IReadOnlyList<string>? Normalize(IEnumerable<string?>? levels)
    {
        if (levels is null)
        {
            return null;
        }

        var normalized = new List<string>();
        foreach (var level in levels)
        {
            var name = level?.Trim().ToLowerInvariant();
            if (IsValidName(name) && !normalized.Contains(name!, StringComparer.Ordinal))
            {
                normalized.Add(name!);
            }
        }

        if (normalized.TrueForAll(name => RankOf(name) >= 0))
        {
            normalized.Sort((left, right) => RankOf(left).CompareTo(RankOf(right)));
        }

        return normalized.Count > MaxCount ? normalized.GetRange(0, MaxCount) : normalized;
    }
}
