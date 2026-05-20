// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

internal static class RepositoryPathSearchExecutor
{
    public static async Task<PathSearchResult> ExecuteAsync(
        PathSearchRequest request,
        string sourceBranch,
        string? targetBranch,
        IReadOnlyList<ChangedPathSnapshot>? changedPathSnapshots,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> loadFileTreeAsync,
        Func<string, string> normalizeBranch,
        Func<string, string> normalizePath,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(loadFileTreeAsync);
        ArgumentNullException.ThrowIfNull(normalizeBranch);
        ArgumentNullException.ThrowIfNull(normalizePath);

        var queryText = request.QueryText?.Trim() ?? string.Empty;
        var matchMode = NormalizeMatchMode(request.MatchMode);
        var filters = RepositoryDiscoveryHelpers.NormalizeFilters(request.Filters);
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return new PathSearchResult(
                RepositorySearchStatuses.InvalidRequest,
                request.BranchSide,
                request.PathScope,
                matchMode,
                filters,
                [],
                [new RepositorySearchLimitation(null, RepositorySearchLimitationReasons.InvalidRegex, "Path search query text is required.")],
                false);
        }

        if (!IsSupportedMode(matchMode))
        {
            return new PathSearchResult(
                RepositorySearchStatuses.InvalidRequest,
                request.BranchSide,
                request.PathScope,
                matchMode,
                filters,
                [],
                [
                    new RepositorySearchLimitation(
                        null, RepositorySearchLimitationReasons.UnsupportedSearchMode, $"Unsupported path search mode '{request.MatchMode}'."),
                ],
                false);
        }

        var candidateResolution = await RepositoryDiscoveryHelpers.ResolveCandidatePathsAsync(
            request.BranchSide,
            request.PathScope,
            filters,
            sourceBranch,
            targetBranch,
            changedPathSnapshots,
            loadFileTreeAsync,
            normalizeBranch,
            normalizePath,
            ct);

        if (candidateResolution.Branch is null)
        {
            return new PathSearchResult(
                RepositorySearchStatuses.InvalidRequest,
                request.BranchSide,
                request.PathScope,
                matchMode,
                filters,
                [],
                candidateResolution.Limitations,
                false);
        }

        var limitations = candidateResolution.Limitations.ToList();
        var matchingPaths = candidateResolution.Paths
            .Where(path => PathMatches(path, queryText, matchMode))
            .Select((path, index) => new PathSearchMatch(path, RepositoryDiscoveryHelpers.InferLanguage(path), index + 1))
            .ToList();
        var truncated = matchingPaths.Count > RepositoryDiscoveryHelpers.MaxReturnedMatches;
        if (truncated)
        {
            matchingPaths = matchingPaths.Take(RepositoryDiscoveryHelpers.MaxReturnedMatches).ToList();
            limitations.Add(
                new RepositorySearchLimitation(
                    null,
                    RepositorySearchLimitationReasons.ResultTruncated,
                    $"Only the first {RepositoryDiscoveryHelpers.MaxReturnedMatches} matching paths were returned."));
        }

        var status = matchingPaths.Count > 0
            ? limitations.Count > 0 || truncated ? RepositorySearchStatuses.Partial : RepositorySearchStatuses.Success
            : limitations.Count > 0
                ? RepositorySearchStatuses.Partial
                : RepositorySearchStatuses.NoMatch;

        return new PathSearchResult(
            status,
            request.BranchSide,
            request.PathScope,
            matchMode,
            filters,
            matchingPaths.AsReadOnly(),
            limitations.AsReadOnly(),
            truncated);
    }

    private static string NormalizeMatchMode(string? matchMode)
    {
        return string.IsNullOrWhiteSpace(matchMode)
            ? PathSearchModes.Contains
            : matchMode.Trim().ToLowerInvariant();
    }

    private static bool IsSupportedMode(string matchMode)
    {
        return matchMode is PathSearchModes.Contains or PathSearchModes.ExactName or PathSearchModes.ExactPathPrefix;
    }

    private static bool PathMatches(string path, string queryText, string matchMode)
    {
        return matchMode switch
        {
            PathSearchModes.ExactName => string.Equals(Path.GetFileName(path), queryText, StringComparison.OrdinalIgnoreCase),
            PathSearchModes.ExactPathPrefix => path.StartsWith(queryText.TrimStart('/'), StringComparison.OrdinalIgnoreCase),
            _ => path.Contains(queryText, StringComparison.OrdinalIgnoreCase),
        };
    }
}
