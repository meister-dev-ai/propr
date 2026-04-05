// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.FileSystemGlobbing;

namespace MeisterProPR.Application.ValueObjects;

/// <summary>
///     Carries file exclusion patterns used to skip certain files during AI review.
///     Patterns are sourced from <c>.meister-propr/exclude</c> on the target branch, or fall back to
///     <see cref="Default" /> built-in patterns when the file is absent.
/// </summary>
public sealed class ReviewExclusionRules
{
    /// <summary>
    ///     Built-in default patterns applied when the repository does not provide a
    ///     <c>.meister-propr/exclude</c> file. Targets the most universally safe generated-file
    ///     types: EF Core migration designer snapshots.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultPatterns =
    [
        "**/Migrations/*.Designer.cs",
        "**/Migrations/*ModelSnapshot.cs",
    ];

    private readonly Matcher? _matcher;
    private readonly Matcher[] _perPatternMatchers;

    private ReviewExclusionRules(IReadOnlyList<string> patterns, bool isDefault)
    {
        this.Patterns = patterns;
        this.IsDefault = isDefault;

        if (patterns.Count > 0)
        {
            this._matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            this._matcher.AddIncludePatterns(patterns);
            this._perPatternMatchers = patterns
                .Select(p =>
                {
                    var m = new Matcher(StringComparison.OrdinalIgnoreCase);
                    m.AddInclude(p);
                    return m;
                })
                .ToArray();
        }
        else
        {
            this._perPatternMatchers = [];
        }
    }

    /// <summary>
    ///     Represents no exclusions (empty pattern list). Used when the repository explicitly
    ///     provides an empty <c>exclude</c> file, or before the exclusion fetch completes.
    /// </summary>
    public static ReviewExclusionRules Empty { get; } = new([], false);

    /// <summary>
    ///     Returns an instance populated with the built-in default patterns.
    ///     Applied automatically when no <c>.meister-propr/exclude</c> file exists on the target branch.
    /// </summary>
    public static ReviewExclusionRules Default { get; } = new(DefaultPatterns, true);

    /// <summary>Glob patterns used for exclusion matching.</summary>
    public IReadOnlyList<string> Patterns { get; }

    /// <summary>
    ///     <see langword="true" /> when this instance was constructed from built-in defaults
    ///     because no per-repo <c>exclude</c> file was found.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    ///     <see langword="true" /> when at least one exclusion pattern is configured.
    ///     Always <see langword="false" /> for <see cref="Empty" />.
    /// </summary>
    public bool HasPatterns => this.Patterns.Count > 0;

    /// <summary>
    ///     Creates a <see cref="ReviewExclusionRules" /> instance from a list of glob patterns
    ///     parsed from a repository's <c>.meister-propr/exclude</c> file.
    /// </summary>
    public static ReviewExclusionRules FromPatterns(IReadOnlyList<string> patterns) =>
        new(patterns, false);

    /// <summary>
    ///     Returns <see langword="true" /> if <paramref name="filePath" /> matches any exclusion pattern.
    ///     Matching is case-insensitive. Returns <see langword="false" /> when no patterns are configured.
    /// </summary>
    /// <param name="filePath">Relative file path to test (e.g. <c>src/Migrations/V1.Designer.cs</c>).</param>
    public bool Matches(string filePath)
    {
        if (this._matcher is null)
        {
            return false;
        }

        // Matcher requires forward slashes and relative paths.
        // ADO returns paths with a leading '/' (e.g. "/openapi.json"); strip it so that
        // anchored patterns like "openapi.json" match as expected. Patterns that already
        // use "**/" are unaffected since ** absorbs any leading sNo itegments either way.
        var normalised = filePath.Replace('\\', '/').TrimStart('/');
        return this._matcher.Match(normalised).HasMatches;
    }

    /// <summary>
    ///     Returns a human-readable description of the first matching pattern, or <see langword="null"/>
    ///     when <paramref name="filePath"/> does not match any pattern.
    /// </summary>
    public string? GetMatchingPattern(string filePath)
    {
        if (this._matcher is null)
        {
            return null;
        }

        var normalised = filePath.Replace('\\', '/').TrimStart('/');
        for (var i = 0; i < this.Patterns.Count; i++)
        {
            if (this._perPatternMatchers[i].Match(normalised).HasMatches)
            {
                return this.Patterns[i];
            }
        }

        return null;
    }
}
