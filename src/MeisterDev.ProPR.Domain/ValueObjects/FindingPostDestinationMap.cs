// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.ObjectModel;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     The keys and the lookup of a client's destination per finding type. A key is a core type slug, for example
///     <c>security</c>, or a core type slug with a qualifier, for example <c>documentation-tests:missing</c>. A type
///     without an entry is posted as a comment.
/// </summary>
public static class FindingPostDestinationMap
{
    /// <summary>The character between the type slug and the qualifier of a key.</summary>
    public const char QualifierSeparator = ':';

    /// <summary>
    ///     The destinations of a client that has not changed the setting: requests for missing tests and
    ///     documentation are listed in the review summary, and every other finding is posted as a comment.
    /// </summary>
    public static IReadOnlyDictionary<string, FindingPostDestination> Default { get; } = Normalize(
    [
        new KeyValuePair<string, FindingPostDestination>(
            KeyFor(FindingTypeTaxonomy.DocumentationTests, CodeInsightFindingQualifier.Missing),
            FindingPostDestination.Summary),
    ]);

    /// <summary>An empty map, which posts every finding as a comment.</summary>
    public static IReadOnlyDictionary<string, FindingPostDestination> Empty { get; } =
        new ReadOnlyDictionary<string, FindingPostDestination>(new Dictionary<string, FindingPostDestination>(StringComparer.Ordinal));

    /// <summary>Returns the key of a core type, with the qualifier when one is given.</summary>
    /// <param name="coreSlug">A core type slug.</param>
    /// <param name="qualifier">The qualifier, or <see langword="null" /> for the key of the type alone.</param>
    public static string KeyFor(string coreSlug, CodeInsightFindingQualifier? qualifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coreSlug);

        return qualifier is { } value
            ? $"{coreSlug}{QualifierSeparator}{QualifierName(value)}"
            : coreSlug;
    }

    /// <summary>
    ///     Returns the canonical form of <paramref name="key" />: the core type slug as the taxonomy writes it, and the
    ///     qualifier in lower case. Case and surrounding whitespace are ignored.
    /// </summary>
    /// <param name="key">The key to read.</param>
    /// <param name="normalized">The canonical key, or an empty string when the key is not valid.</param>
    /// <returns>Whether the key names a core type, optionally with a defined qualifier.</returns>
    public static bool TryNormalizeKey(string? key, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var parts = key.Split(QualifierSeparator);
        if (parts.Length > 2 || FindingTypeTaxonomy.Find(parts[0]) is not { } type)
        {
            return false;
        }

        if (parts.Length == 1)
        {
            normalized = type.Slug;
            return true;
        }

        if (!TryParseQualifier(parts[1], out var qualifier))
        {
            return false;
        }

        normalized = KeyFor(type.Slug, qualifier);
        return true;
    }

    /// <summary>
    ///     Returns the canonical form of a destination map. Keys are normalized, entries with a key that is not valid
    ///     or a destination that is not defined are dropped, and a later entry replaces an earlier one with the same
    ///     key. An entry that sets the key of a type alone to <see cref="FindingPostDestination.Comment" /> is dropped,
    ///     because a type without an entry is posted as a comment. An entry with a qualifier keeps the destination
    ///     <see cref="FindingPostDestination.Comment" />, because it takes precedence over the key of its type.
    /// </summary>
    /// <param name="entries">The entries to normalize, or <see langword="null" /> for none.</param>
    public static IReadOnlyDictionary<string, FindingPostDestination> Normalize(IEnumerable<KeyValuePair<string, FindingPostDestination>>? entries)
    {
        var normalized = new SortedDictionary<string, FindingPostDestination>(StringComparer.Ordinal);
        foreach (var (key, destination) in entries ?? [])
        {
            if (!Enum.IsDefined(destination) || !TryNormalizeKey(key, out var canonical))
            {
                continue;
            }

            if (destination == FindingPostDestination.Comment && !canonical.Contains(QualifierSeparator, StringComparison.Ordinal))
            {
                normalized.Remove(canonical);
                continue;
            }

            normalized[canonical] = destination;
        }

        return new ReadOnlyDictionary<string, FindingPostDestination>(normalized);
    }

    /// <summary>
    ///     Returns the destination of a finding. For each core type of the finding, the key with the finding's
    ///     qualifier is looked up first and the key of the type second, and a type without an entry has the destination
    ///     <see cref="FindingPostDestination.Comment" />. The finding takes the most visible destination among its
    ///     types. A finding without a supported classification is posted as a comment.
    /// </summary>
    /// <param name="destinations">The client's destination map.</param>
    /// <param name="classification">The classification of the finding, or <see langword="null" />.</param>
    public static FindingPostDestination Resolve(
        IReadOnlyDictionary<string, FindingPostDestination>? destinations,
        FindingTypeClassification? classification)
    {
        if (destinations is not { Count: > 0 } || classification is not { HasSupportedTypes: true })
        {
            return FindingPostDestination.Comment;
        }

        FindingPostDestination? resolved = null;
        foreach (var slug in CoreSlugsOf(classification))
        {
            var destination = ResolveType(destinations, slug, classification.Qualifier);
            if (resolved is null || destination < resolved)
            {
                resolved = destination;
            }
        }

        return resolved ?? FindingPostDestination.Comment;
    }

    /// <summary>
    ///     Returns the destination of one core type for a finding with the given qualifier: the entry of the key with
    ///     the qualifier, otherwise the entry of the key of the type, otherwise <see cref="FindingPostDestination.Comment" />.
    /// </summary>
    /// <param name="destinations">The client's destination map.</param>
    /// <param name="coreSlug">A core type slug.</param>
    /// <param name="qualifier">The qualifier of the finding, or <see langword="null" />.</param>
    public static FindingPostDestination ResolveType(
        IReadOnlyDictionary<string, FindingPostDestination>? destinations,
        string coreSlug,
        CodeInsightFindingQualifier? qualifier)
    {
        if (destinations is not { Count: > 0 })
        {
            return FindingPostDestination.Comment;
        }

        if (qualifier is { } value && Enum.IsDefined(value) && destinations.TryGetValue(KeyFor(coreSlug, value), out var qualified))
        {
            return qualified;
        }

        return destinations.TryGetValue(coreSlug, out var typeDestination)
            ? typeDestination
            : FindingPostDestination.Comment;
    }

    /// <summary>Returns the distinct core types of a classification as the taxonomy writes them, in their order.</summary>
    /// <param name="classification">The classification of a finding.</param>
    public static IReadOnlyList<string> CoreSlugsOf(FindingTypeClassification classification)
    {
        ArgumentNullException.ThrowIfNull(classification);

        return (classification.CoreSlugs ?? [])
            .Select(FindingTypeTaxonomy.Find)
            .Where(type => type is not null)
            .Select(type => type!.Slug)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     Returns the core types whose key alone has the destination <see cref="FindingPostDestination.ReviewOnly" />,
    ///     in canonical order. This is the list of finding types that are not posted, as the API reported it before a
    ///     destination could be chosen per type.
    /// </summary>
    /// <param name="destinations">The client's destination map.</param>
    public static IReadOnlyList<string> ReviewOnlyTypes(IReadOnlyDictionary<string, FindingPostDestination>? destinations)
    {
        if (destinations is not { Count: > 0 })
        {
            return [];
        }

        return FindingTypeTaxonomy.AllSlugs
            .Where(slug => destinations.TryGetValue(slug, out var destination) && destination == FindingPostDestination.ReviewOnly)
            .ToList();
    }

    /// <summary>
    ///     Applies a list of finding types that are not posted, as the API accepted it before a destination could be
    ///     chosen per type. Every listed type gets the destination <see cref="FindingPostDestination.ReviewOnly" />. A
    ///     listed type whose key alone did not have that destination before also loses its keys with a qualifier, so that
    ///     no finding of the type is posted or listed. A listed type whose key alone already had that destination keeps
    ///     its keys with a qualifier, so a client that writes back the list it read leaves the map unchanged. A type that
    ///     is not listed and had the destination <see cref="FindingPostDestination.ReviewOnly" /> is posted as a comment
    ///     again. Other entries stay as they are.
    /// </summary>
    /// <param name="destinations">The client's current destination map.</param>
    /// <param name="reviewOnlyTypes">The core types that are not posted.</param>
    public static IReadOnlyDictionary<string, FindingPostDestination> WithReviewOnlyTypes(
        IReadOnlyDictionary<string, FindingPostDestination>? destinations,
        IEnumerable<string> reviewOnlyTypes)
    {
        ArgumentNullException.ThrowIfNull(reviewOnlyTypes);

        var listed = reviewOnlyTypes
            .Select(FindingTypeTaxonomy.Find)
            .Where(type => type is not null)
            .Select(type => type!.Slug)
            .ToHashSet(StringComparer.Ordinal);
        var updated = new Dictionary<string, FindingPostDestination>(Normalize(destinations), StringComparer.Ordinal);

        foreach (var slug in FindingTypeTaxonomy.AllSlugs)
        {
            if (listed.Contains(slug))
            {
                // The list holds only the key of a type, so it cannot express a key with a qualifier. The keys with a
                // qualifier of a type that was review only before were not written by this list and stay.
                var wasReviewOnly = updated.TryGetValue(slug, out var previous) && previous == FindingPostDestination.ReviewOnly;
                if (!wasReviewOnly)
                {
                    foreach (var qualifiedKey in updated.Keys.Where(key => key.StartsWith(slug + QualifierSeparator, StringComparison.Ordinal)).ToList())
                    {
                        updated.Remove(qualifiedKey);
                    }
                }

                updated[slug] = FindingPostDestination.ReviewOnly;
            }
            else if (updated.TryGetValue(slug, out var destination) && destination == FindingPostDestination.ReviewOnly)
            {
                updated.Remove(slug);
            }
        }

        return Normalize(updated);
    }

    private static string QualifierName(CodeInsightFindingQualifier qualifier)
    {
        return qualifier switch
        {
            CodeInsightFindingQualifier.Missing => "missing",
            CodeInsightFindingQualifier.Incorrect => "incorrect",
            CodeInsightFindingQualifier.Extraneous => "extraneous",
            _ => throw new ArgumentOutOfRangeException(nameof(qualifier), qualifier, "The qualifier is not defined."),
        };
    }

    // Names only: Enum.TryParse would also accept a number such as "1", which is not a qualifier a key can name.
    private static bool TryParseQualifier(string value, out CodeInsightFindingQualifier qualifier)
    {
        foreach (var candidate in Enum.GetValues<CodeInsightFindingQualifier>())
        {
            if (string.Equals(QualifierName(candidate), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                qualifier = candidate;
                return true;
            }
        }

        qualifier = default;
        return false;
    }
}
