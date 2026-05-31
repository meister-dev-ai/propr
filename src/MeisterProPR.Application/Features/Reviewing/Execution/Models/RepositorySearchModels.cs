// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Canonical branch-side values for repository search requests and results.
/// </summary>
public static class RepositorySearchBranchSides
{
    /// <summary>
    ///     The source branch side.
    /// </summary>
    public const string Source = "source";

    /// <summary>
    ///     The target branch side.
    /// </summary>
    public const string Target = "target";
}

/// <summary>
///     Canonical scope values for repository search requests and results.
/// </summary>
public static class RepositorySearchPathScopes
{
    /// <summary>
    ///     The repository scope.
    /// </summary>
    public const string Repository = "repository";

    /// <summary>
    ///     The changed files scope.
    /// </summary>
    public const string ChangedFiles = "changed_files";
}

/// <summary>
///     Canonical status values for repository search results.
/// </summary>
public static class RepositorySearchStatuses
{
    /// <summary>
    ///     The search completed successfully.
    /// </summary>
    public const string Success = "success";

    /// <summary>
    ///     The search found no matches.
    /// </summary>
    public const string NoMatch = "no_match";

    /// <summary>
    ///     The search returned partial results due to limitations.
    /// </summary>
    public const string Partial = "partial";

    /// <summary>
    ///     The search request was invalid.
    /// </summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>
    ///     The search was blocked due to insufficient permissions.
    /// </summary>
    public const string BlockedNotAllowed = "blocked_not_allowed";

    /// <summary>
    ///     The search was blocked because the budget was exhausted.
    /// </summary>
    public const string BlockedBudgetExhausted = "blocked_budget_exhausted";

    /// <summary>
    ///     The search was blocked due to a scope violation.
    /// </summary>
    public const string BlockedScopeViolation = "blocked_scope_violation";
}

/// <summary>
///     Canonical limitation-reason values for repository search results.
/// </summary>
public static class RepositorySearchLimitationReasons
{
    /// <summary>
    ///     The search regex was invalid.
    /// </summary>
    public const string InvalidRegex = "invalid_regex";

    /// <summary>
    ///     The file is missing on the branch.
    /// </summary>
    public const string MissingOnBranch = "missing_on_branch";

    /// <summary>
    ///     The file is binary.
    /// </summary>
    public const string BinaryFile = "binary_file";

    /// <summary>
    ///     The file could not be read.
    /// </summary>
    public const string UnreadableFile = "unreadable_file";

    /// <summary>
    ///     The provider fetch failed.
    /// </summary>
    public const string ProviderFetchFailed = "provider_fetch_failed";

    /// <summary>
    ///     The search result was truncated.
    /// </summary>
    public const string ResultTruncated = "result_truncated";

    /// <summary>
    ///     The request used an unsupported search mode.
    /// </summary>
    public const string UnsupportedSearchMode = "unsupported_search_mode";

    /// <summary>
    ///     A filter excluded a candidate path.
    /// </summary>
    public const string FilterExcluded = "filter_excluded";

    /// <summary>
    ///     The requested file was not found.
    /// </summary>
    public const string FileNotFound = "file_not_found";
}

/// <summary>
///     One agent-issued repository search request.
/// </summary>
public sealed record RepositorySearchRequest(
    string SearchTerm,
    string? FileMask,
    string BranchSide,
    string PathScope);

/// <summary>
///     One changed-path snapshot used to scope changed-files-only repository searches.
/// </summary>
public sealed record ChangedPathSnapshot(
    string Path,
    ChangeType ChangeType,
    bool SourceExists,
    bool TargetExists,
    string? TargetPath = null)
{
    /// <summary>
    ///     Target-side path used when the path differs across branches, such as rename scenarios.
    /// </summary>
    public string EffectiveTargetPath => string.IsNullOrWhiteSpace(this.TargetPath) ? this.Path : this.TargetPath;

    /// <summary>
    ///     Creates a changed-path snapshot from a live pull-request changed file.
    /// </summary>
    public static ChangedPathSnapshot FromChangedFile(ChangedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return file.ChangeType switch
        {
            ChangeType.Add => new ChangedPathSnapshot(file.Path, file.ChangeType, true, false),
            ChangeType.Delete => new ChangedPathSnapshot(file.Path, file.ChangeType, false, true, file.OriginalPath ?? file.Path),
            ChangeType.Rename => new ChangedPathSnapshot(file.Path, file.ChangeType, true, true, file.OriginalPath ?? file.Path),
            _ => new ChangedPathSnapshot(file.Path, file.ChangeType, true, true),
        };
    }

    /// <summary>
    ///     Creates a changed-path snapshot from an offline fixture changed file.
    /// </summary>
    public static ChangedPathSnapshot FromFixtureChangedFile(FixtureChangedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return file.ChangeType switch
        {
            ChangeType.Add => new ChangedPathSnapshot(file.Path, file.ChangeType, true, false),
            ChangeType.Delete => new ChangedPathSnapshot(file.Path, file.ChangeType, false, true, file.OriginalPath ?? file.Path),
            ChangeType.Rename => new ChangedPathSnapshot(file.Path, file.ChangeType, true, true, file.OriginalPath ?? file.Path),
            _ => new ChangedPathSnapshot(file.Path, file.ChangeType, true, true),
        };
    }
}

/// <summary>
///     One regex hit returned by a repository search.
/// </summary>
public sealed record RepositorySearchMatch(string FilePath, int LineNumber, string LineText);

/// <summary>
///     One non-fatal repository-search limitation.
/// </summary>
public sealed record RepositorySearchLimitation(string? Path, string Reason, string Message);

/// <summary>
///     Structured repository-search result returned to the AI review loop.
/// </summary>
public sealed record RepositorySearchResult(
    string Status,
    string BranchSide,
    string PathScope,
    string? FileMaskApplied,
    IReadOnlyList<RepositorySearchMatch> Matches,
    IReadOnlyList<RepositorySearchLimitation> Limitations,
    bool Truncated,
    IReadOnlyList<ProtocolEventPhaseTiming>? PhaseTimings = null)
    : IToolExecutionTimingCarrier
{
    /// <summary>
    ///     Creates a blocked repository-search result that stays serializable and auditable.
    /// </summary>
    public static RepositorySearchResult CreateBlocked(RepositorySearchRequest request, string status)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new RepositorySearchResult(
            status,
            request.BranchSide,
            request.PathScope,
            request.FileMask,
            [],
            [],
            false);
    }
}
