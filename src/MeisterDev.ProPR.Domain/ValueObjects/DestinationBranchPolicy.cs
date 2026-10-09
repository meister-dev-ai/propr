// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>Validated destination branch patterns; an empty set permits all branches.</summary>
public sealed class DestinationBranchPolicy
{
    // Effective path segments exclude empty and dot segments; Patterns retains normalized spelling.
    private readonly string[][] _segments;

    /// <summary>Maximum number of destination patterns.</summary>
    public const int MaximumPatterns = 100;

    /// <summary>Maximum input length of each pattern.</summary>
    public const int MaximumPatternLength = 512;

    private DestinationBranchPolicy(IReadOnlyList<string> patterns)
    {
        this.Patterns = patterns;
        this._segments = patterns.Select(EffectiveSegments).ToArray();
    }

    /// <summary>Normalized patterns, with branch reference prefixes removed.</summary>
    public IReadOnlyList<string> Patterns { get; }

    /// <summary>Validates and copies patterns without changing the supplied collection.</summary>
    public static DestinationBranchPolicy Create(IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Count > MaximumPatterns)
        {
            throw new ArgumentException("Too many destination branch patterns.", nameof(patterns));
        }

        var normalized = new List<string>(patterns.Count);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            if (pattern is null || pattern.Length > MaximumPatternLength || pattern.Any(char.IsControl))
            {
                throw new ArgumentException("Invalid destination branch pattern.", nameof(patterns));
            }

            var value = Normalize(pattern);
            var segments = EffectiveSegments(value);
            if (string.IsNullOrWhiteSpace(value) || segments.Length == 0 || !unique.Add(string.Join('/', segments)))
            {
                throw new ArgumentException("Invalid or duplicate destination branch pattern.", nameof(patterns));
            }

            normalized.Add(value);
        }

        return new DestinationBranchPolicy(normalized.AsReadOnly());
    }

    /// <summary>Checks patterns and returns a policy when they are valid.</summary>
    public static bool TryCreate(IReadOnlyList<string>? patterns, out DestinationBranchPolicy? policy)
    {
        try
        {
            policy = Create(patterns ?? []);
            return true;
        }
        catch (ArgumentException)
        {
            policy = null;
            return false;
        }
    }

    /// <summary>Matches case-insensitive path globs; restricted policies reject unavailable branches.</summary>
    public bool Matches(string? branch)
    {
        if (this.Patterns.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(branch) || branch.Any(char.IsControl))
        {
            return false;
        }

        var normalized = Normalize(branch);
        if (normalized.Length == 0)
        {
            return false;
        }

        var segments = normalized.Split('/');
        return this._segments.Any(pattern => MatchesPath(pattern, segments));
    }

    private static string[] EffectiveSegments(string pattern) =>
        pattern.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(segment => segment != ".").ToArray();

    private static string Normalize(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase) ? trimmed[11..] : trimmed;
    }

    /// <summary>Matches pattern prefixes against branch prefixes using two Boolean dynamic-programming rows.</summary>
    /// <remarks>
    /// Each row records which branch-prefix lengths match the consumed pattern prefix.
    /// An ordinary segment matches when the previous row matches the preceding branch prefix
    /// and the segment matches the next branch segment.
    /// An interior ** matches the same prefix from the previous row or extends the current row's
    /// preceding prefix, consuming zero or more branch segments.
    /// A terminal ** starts from the previous row's preceding prefix, consuming at least one segment.
    /// Space is O(branch segments). The state count is O(pattern segments × branch segments),
    /// with additional literal and wildcard matching within ordinary segments.
    /// </remarks>
    private static bool MatchesPath(string[] pattern, string[] branch)
    {
        var previous = new bool[branch.Length + 1];
        previous[0] = true;
        for (var patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
        {
            var segment = pattern[patternIndex];
            var terminalRecursive = segment == "**" && patternIndex == pattern.Length - 1;
            var current = new bool[branch.Length + 1];
            if (segment == "**" && !terminalRecursive)
            {
                current[0] = previous[0];
            }

            for (var index = 1; index <= branch.Length; index++)
            {
                current[index] = segment == "**"
                    ? previous[terminalRecursive ? index - 1 : index] || current[index - 1]
                    : previous[index - 1] && MatchesSegment(segment, branch[index - 1]);
            }

            previous = current;
        }

        return previous[branch.Length];
    }

    /// <summary>Matches literal fragments and single-segment wildcards with a latest-star retry position.</summary>
    /// <remarks>
    /// A mismatch extends the latest star by one value position and retries its following literal fragment.
    /// Complete literal fragments use OrdinalIgnoreCase span comparison to preserve supplementary Unicode casing.
    /// </remarks>
    private static bool MatchesSegment(string pattern, string value)
    {
        var patternIndex = 0;
        var valueIndex = 0;
        var starIndex = -1;
        var retryIndex = 0;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryIndex = valueIndex;
            }
            else
            {
                var literalEnd = pattern.IndexOf('*', patternIndex);
                if (literalEnd < 0)
                {
                    literalEnd = pattern.Length;
                }

                var literalLength = literalEnd - patternIndex;
                // Complete literal fragments preserve ordinal casing for supplementary Unicode characters.
                if (literalLength > 0 && literalLength <= value.Length - valueIndex &&
                    MemoryExtensions.Equals(
                        pattern.AsSpan(patternIndex, literalLength), value.AsSpan(valueIndex, literalLength),
                        StringComparison.OrdinalIgnoreCase))
                {
                    patternIndex = literalEnd;
                    valueIndex += literalLength;
                }
                else if (starIndex >= 0)
                {
                    patternIndex = starIndex + 1;
                    valueIndex = ++retryIndex;
                }
                else
                {
                    return false;
                }
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}
