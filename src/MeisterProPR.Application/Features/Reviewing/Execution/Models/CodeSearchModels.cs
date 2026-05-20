// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Canonical code-search mode values used by review-context discovery tools.
/// </summary>
public static class CodeSearchModes
{
    public const string ExactIdentifier = "exact_identifier";
    public const string ExactPhrase = "exact_phrase";
    public const string Regex = "regex";
    public const string RelatedSymbol = "related_symbol";
    public const string RelatedConfigKey = "related_config_key";
    public const string RelatedRoute = "related_route";
    public const string RelatedDependencyRegistration = "related_dependency_registration";
    public const string RelatedExceptionOrLog = "related_exception_or_log";
}

/// <summary>
///     Canonical path-search mode values used by review-context discovery tools.
/// </summary>
public static class PathSearchModes
{
    public const string Contains = "contains";
    public const string ExactName = "exact_name";
    public const string ExactPathPrefix = "exact_path_prefix";
}

/// <summary>
///     Optional narrowing criteria shared by code search and path search.
/// </summary>
public sealed record CodeSearchFilterSet(
    string? Language = null,
    string? FileGlob = null,
    string? PathPrefix = null,
    bool ExcludeGenerated = false,
    bool ExcludeTests = false);

/// <summary>
///     One branch-aware code-search request.
/// </summary>
public sealed record CodeSearchRequest(
    string QueryText,
    string SearchMode,
    string BranchSide,
    string PathScope,
    CodeSearchFilterSet? Filters = null);

/// <summary>
///     One ranked code-search hit.
/// </summary>
public sealed record CodeSearchMatch(
    string FilePath,
    int? LineNumber,
    string MatchText,
    string? Language,
    int Rank,
    bool Truncated);

/// <summary>
///     Structured outcome of one code-search request.
/// </summary>
public sealed record CodeSearchResult(
    string Status,
    string BranchSide,
    string PathScope,
    string SearchMode,
    CodeSearchFilterSet? FiltersApplied,
    IReadOnlyList<CodeSearchMatch> Matches,
    IReadOnlyList<RepositorySearchLimitation> Limitations,
    bool Truncated)
{
    /// <summary>Creates a blocked result that keeps the request shape serializable.</summary>
    public static CodeSearchResult CreateBlocked(CodeSearchRequest request, string status)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new CodeSearchResult(
            status,
            request.BranchSide,
            request.PathScope,
            request.SearchMode,
            request.Filters,
            [],
            [],
            false);
    }
}

/// <summary>
///     One branch-aware path-search request.
/// </summary>
public sealed record PathSearchRequest(
    string QueryText,
    string MatchMode,
    string BranchSide,
    string PathScope,
    CodeSearchFilterSet? Filters = null);

/// <summary>
///     One ranked path-search hit.
/// </summary>
public sealed record PathSearchMatch(string FilePath, string? Language, int Rank);

/// <summary>
///     Structured outcome of one path-search request.
/// </summary>
public sealed record PathSearchResult(
    string Status,
    string BranchSide,
    string PathScope,
    string MatchMode,
    CodeSearchFilterSet? FiltersApplied,
    IReadOnlyList<PathSearchMatch> Paths,
    IReadOnlyList<RepositorySearchLimitation> Limitations,
    bool Truncated)
{
    /// <summary>Creates a blocked result that keeps the request shape serializable.</summary>
    public static PathSearchResult CreateBlocked(PathSearchRequest request, string status)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new PathSearchResult(
            status,
            request.BranchSide,
            request.PathScope,
            request.MatchMode,
            request.Filters,
            [],
            [],
            false);
    }
}
