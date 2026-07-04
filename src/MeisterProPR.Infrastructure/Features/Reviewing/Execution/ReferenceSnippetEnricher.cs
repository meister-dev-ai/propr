// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.CodeAnalysis;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

/// <summary>
///     Shared enrichment for the structural reference/definition tools. Extracts a bounded, single-line
///     snippet for a matched line and resolves the enclosing definition per line by reusing the analyzer's
///     <see cref="IStructuralCodeAnalyzer.ResolveEnclosingDefinitionsAsync" />. Reuses the file content the
///     tool already holds; performs no new file reads and never throws for input/parse problems.
/// </summary>
internal static class ReferenceSnippetEnricher
{
    /// <summary>
    ///     Upper bound (in characters) on the single-line snippet carried alongside a reference/definition
    ///     site. Keeps tool results and prefetch evidence compact while still showing the matched line.
    /// </summary>
    public const int MaxSnippetChars = 200;

    private static readonly IReadOnlyDictionary<int, EnclosingDefinition> EmptyMap =
        new Dictionary<int, EnclosingDefinition>();

    /// <summary>
    ///     Splits file content into lines once so per-line snippet extraction stays cheap.
    /// </summary>
    public static string[] SplitLines(string content)
    {
        return content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }

    /// <summary>
    ///     Returns the 1-based <paramref name="line" /> from <paramref name="contentLines" />,
    ///     whitespace-collapsed to a single line and bounded to <see cref="MaxSnippetChars" />.
    ///     Empty string when the line is out of range or blank.
    /// </summary>
    public static string ExtractSnippet(string[] contentLines, int line)
    {
        if (contentLines.Length == 0 || line < 1 || line > contentLines.Length)
        {
            return string.Empty;
        }

        var collapsed = CollapseWhitespace(contentLines[line - 1]);
        if (collapsed.Length <= MaxSnippetChars)
        {
            return collapsed;
        }

        // Keep the total length at or under the cap, including the single-character ellipsis marker.
        return string.Concat(collapsed.AsSpan(0, MaxSnippetChars - 1), "…");
    }

    /// <summary>
    ///     Maps each of <paramref name="lines" /> to the innermost definition that encloses it, by asking the
    ///     analyzer for the definitions enclosing those lines. Lines with no enclosing definition are absent
    ///     from the map. Fail-soft: returns an empty map on any analyzer fault so callers simply omit the
    ///     enclosing symbol.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, EnclosingDefinition>> ResolveEnclosingByLineAsync(
        IStructuralCodeAnalyzer analyzer,
        string path,
        SupportedLanguage language,
        string content,
        IReadOnlyList<int> lines,
        CancellationToken ct)
    {
        if (lines.Count == 0)
        {
            return EmptyMap;
        }

        var ranges = lines.Select(static line => new ChangedLineRange(line, line)).ToList();

        IReadOnlyList<EnclosingDefinition> definitions;
        try
        {
            definitions = await analyzer
                .ResolveEnclosingDefinitionsAsync(new StructuralParseRequest(path, language, content, ranges), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return EmptyMap;
        }

        if (definitions.Count == 0)
        {
            return EmptyMap;
        }

        var map = new Dictionary<int, EnclosingDefinition>();
        foreach (var line in lines)
        {
            if (map.ContainsKey(line))
            {
                continue;
            }

            // Pick the tightest span that contains the line: the analyzer keeps only the innermost
            // definition per range, but several ranges can each contribute one candidate here.
            EnclosingDefinition? best = null;
            foreach (var def in definitions)
            {
                if (line < def.StartLine || line > def.EndLine)
                {
                    continue;
                }

                if (best is null || def.EndLine - def.StartLine < best.EndLine - best.StartLine)
                {
                    best = def;
                }
            }

            if (best is not null)
            {
                map[line] = best;
            }
        }

        return map;
    }

    private static string CollapseWhitespace(string raw)
    {
        // Split on any run of whitespace and drop empties, then rejoin with single spaces. This trims
        // leading/trailing whitespace and normalizes the match to a single line.
        var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? string.Empty : string.Join(' ', parts);
    }
}
