// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using Microsoft.Extensions.FileSystemGlobbing;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

internal sealed record CandidatePathResolution(
    string? Branch,
    IReadOnlyList<string> Paths,
    IReadOnlyList<RepositorySearchLimitation> Limitations);

internal static class RepositoryDiscoveryHelpers
{
    public const int MaxReturnedMatches = 50;

    public static async Task<CandidatePathResolution> ResolveCandidatePathsAsync(
        string branchSide,
        string pathScope,
        CodeSearchFilterSet? filters,
        string sourceBranch,
        string? targetBranch,
        IReadOnlyList<ChangedPathSnapshot>? changedPathSnapshots,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> loadFileTreeAsync,
        Func<string, string> normalizeBranch,
        Func<string, string> normalizePath,
        CancellationToken ct)
    {
        var limitations = new List<RepositorySearchLimitation>();
        var normalizedFilters = NormalizeFilters(filters);
        var branch = ResolveBranch(branchSide, sourceBranch, targetBranch, normalizeBranch);
        if (branch is null)
        {
            limitations.Add(
                new RepositorySearchLimitation(
                    null,
                    RepositorySearchLimitationReasons.MissingOnBranch,
                    "The requested branch is not available in the current review context."));
            return new CandidatePathResolution(null, [], limitations.AsReadOnly());
        }

        IReadOnlyList<string> rawPaths;
        if (string.Equals(pathScope, RepositorySearchPathScopes.Repository, StringComparison.Ordinal))
        {
            try
            {
                rawPaths = await loadFileTreeAsync(branch, ct);
            }
            catch (Exception ex)
            {
                limitations.Add(new RepositorySearchLimitation(null, RepositorySearchLimitationReasons.ProviderFetchFailed, ex.Message));
                return new CandidatePathResolution(branch, [], limitations.AsReadOnly());
            }

            rawPaths = rawPaths
                .Select(path => NormalizeRepositoryPath(path, normalizePath))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly();
        }
        else if (string.Equals(pathScope, RepositorySearchPathScopes.ChangedFiles, StringComparison.Ordinal))
        {
            rawPaths = ResolveChangedScopeCandidates(branchSide, changedPathSnapshots, normalizePath, limitations);
        }
        else
        {
            limitations.Add(
                new RepositorySearchLimitation(
                    null,
                    RepositorySearchLimitationReasons.UnsupportedSearchMode,
                    $"Unsupported path scope '{pathScope}'."));
            return new CandidatePathResolution(branch, [], limitations.AsReadOnly());
        }

        var filteredPaths = ApplyFilters(rawPaths, normalizedFilters, out var excludedCount);
        if (excludedCount > 0)
        {
            limitations.Add(
                new RepositorySearchLimitation(
                    null,
                    RepositorySearchLimitationReasons.FilterExcluded,
                    $"{excludedCount} candidate path(s) were excluded by filters."));
        }

        return new CandidatePathResolution(branch, filteredPaths, limitations.AsReadOnly());
    }

    public static string? ResolveBranch(
        string branchSide,
        string sourceBranch,
        string? targetBranch,
        Func<string, string> normalizeBranch)
    {
        if (string.Equals(branchSide, RepositorySearchBranchSides.Target, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(targetBranch) ? null : normalizeBranch(targetBranch);
        }

        if (string.Equals(branchSide, RepositorySearchBranchSides.Source, StringComparison.Ordinal))
        {
            return normalizeBranch(sourceBranch);
        }

        return null;
    }

    public static string NormalizeRepositoryPath(string path, Func<string, string> normalizePath)
    {
        return normalizePath(path).Replace('\\', '/').TrimStart('/');
    }

    public static CodeSearchFilterSet? NormalizeFilters(CodeSearchFilterSet? filters)
    {
        if (filters is null)
        {
            return null;
        }

        var language = string.IsNullOrWhiteSpace(filters.Language) ? null : filters.Language.Trim().TrimStart('.').ToLowerInvariant();
        var fileGlob = string.IsNullOrWhiteSpace(filters.FileGlob) ? null : filters.FileGlob.Trim();
        var pathPrefix = string.IsNullOrWhiteSpace(filters.PathPrefix)
            ? null
            : filters.PathPrefix.Replace('\\', '/').Trim().TrimStart('/');

        return new CodeSearchFilterSet(
            language,
            fileGlob,
            pathPrefix,
            filters.ExcludeGenerated,
            filters.ExcludeTests);
    }

    public static string? InferLanguage(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".cs" => "csharp",
            ".fs" => "fsharp",
            ".vb" => "visualbasic",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
            ".vue" => "vue",
            ".json" => "json",
            ".md" or ".markdown" => "markdown",
            ".yml" or ".yaml" => "yaml",
            ".xml" or ".csproj" or ".props" or ".targets" => "xml",
            ".sql" => "sql",
            ".sh" => "shell",
            ".ps1" => "powershell",
            ".py" => "python",
            ".java" => "java",
            ".go" => "go",
            ".rs" => "rust",
            _ => string.IsNullOrEmpty(extension) ? null : extension.TrimStart('.'),
        };
    }

    public static bool MatchesFilters(string path, CodeSearchFilterSet? filters)
    {
        var normalizedFilters = NormalizeFilters(filters);
        if (normalizedFilters is null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(normalizedFilters.FileGlob) && !MatchesFileMask(path, normalizedFilters.FileGlob))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(normalizedFilters.PathPrefix) &&
            !path.StartsWith(normalizedFilters.PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(normalizedFilters.Language))
        {
            var language = InferLanguage(path);
            var extension = Path.GetExtension(path).TrimStart('.');
            if (!string.Equals(language, normalizedFilters.Language, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, normalizedFilters.Language, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (normalizedFilters.ExcludeGenerated && IsGeneratedPath(path))
        {
            return false;
        }

        return !normalizedFilters.ExcludeTests || !IsTestPath(path);
    }

    public static bool MatchesFileMask(string path, string? fileMask)
    {
        if (string.IsNullOrWhiteSpace(fileMask))
        {
            return true;
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(fileMask);
        return matcher.Match(path).HasMatches;
    }

    private static IReadOnlyList<string> ApplyFilters(
        IReadOnlyList<string> paths,
        CodeSearchFilterSet? filters,
        out int excludedCount)
    {
        var filtered = new List<string>(paths.Count);
        excludedCount = 0;

        foreach (var path in paths)
        {
            if (MatchesFilters(path, filters))
            {
                filtered.Add(path);
            }
            else
            {
                excludedCount++;
            }
        }

        return filtered
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    private static IReadOnlyList<string> ResolveChangedScopeCandidates(
        string branchSide,
        IReadOnlyList<ChangedPathSnapshot>? changedPathSnapshots,
        Func<string, string> normalizePath,
        ICollection<RepositorySearchLimitation> limitations)
    {
        if (changedPathSnapshots is null || changedPathSnapshots.Count == 0)
        {
            return [];
        }

        var candidates = new List<string>(changedPathSnapshots.Count);
        foreach (var snapshot in changedPathSnapshots)
        {
            var candidatePath = string.Equals(branchSide, RepositorySearchBranchSides.Target, StringComparison.Ordinal)
                ? snapshot.EffectiveTargetPath
                : snapshot.Path;
            var normalizedPath = NormalizeRepositoryPath(candidatePath, normalizePath);

            var existsOnBranch = string.Equals(branchSide, RepositorySearchBranchSides.Target, StringComparison.Ordinal)
                ? snapshot.TargetExists
                : snapshot.SourceExists;
            if (!existsOnBranch)
            {
                limitations.Add(
                    new RepositorySearchLimitation(
                        normalizedPath,
                        RepositorySearchLimitationReasons.MissingOnBranch,
                        "The changed path does not exist on the requested branch."));
                continue;
            }

            candidates.Add(normalizedPath);
        }

        return candidates
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    private static bool IsGeneratedPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/generated/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        return normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(".tests/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(".test/", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("test", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("spec", StringComparison.OrdinalIgnoreCase);
    }
}
