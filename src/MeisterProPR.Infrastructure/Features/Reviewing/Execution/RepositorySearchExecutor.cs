// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.RegularExpressions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using Microsoft.Extensions.FileSystemGlobbing;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

internal static class RepositorySearchExecutor
{
    private const int MaxReturnedMatches = 50;

    public static async Task<RepositorySearchResult> ExecuteAsync(
        RepositorySearchRequest request,
        string sourceBranch,
        string? targetBranch,
        IReadOnlyList<ChangedPathSnapshot>? changedPathSnapshots,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> loadFileTreeAsync,
        Func<string, string, CancellationToken, Task<string?>> fetchRawFileContentAsync,
        Func<string, string> normalizeBranch,
        Func<string, string> normalizePath,
        int maxFileSizeBytes,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(loadFileTreeAsync);
        ArgumentNullException.ThrowIfNull(fetchRawFileContentAsync);
        ArgumentNullException.ThrowIfNull(normalizeBranch);
        ArgumentNullException.ThrowIfNull(normalizePath);

        var searchTerm = request.SearchTerm?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return new RepositorySearchResult(
                RepositorySearchStatuses.InvalidRequest,
                request.BranchSide,
                request.PathScope,
                NormalizeFileMask(request.FileMask),
                [],
                [new RepositorySearchLimitation(null, RepositorySearchLimitationReasons.InvalidRegex, "Search term is required.")],
                false);
        }

        Regex regex;
        try
        {
            regex = new Regex(searchTerm, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            return new RepositorySearchResult(
                RepositorySearchStatuses.InvalidRequest,
                request.BranchSide,
                request.PathScope,
                NormalizeFileMask(request.FileMask),
                [],
                [new RepositorySearchLimitation(null, RepositorySearchLimitationReasons.InvalidRegex, ex.Message)],
                false);
        }

        var fileMask = NormalizeFileMask(request.FileMask);
        var branch = ResolveBranch(request.BranchSide, sourceBranch, targetBranch, normalizeBranch);
        if (branch is null)
        {
            return new RepositorySearchResult(
                RepositorySearchStatuses.InvalidRequest,
                request.BranchSide,
                request.PathScope,
                fileMask,
                [],
                [
                    new RepositorySearchLimitation(
                        null, RepositorySearchLimitationReasons.MissingOnBranch, "The requested branch is not available in the current review context."),
                ],
                false);
        }

        var limitations = new List<RepositorySearchLimitation>();
        IReadOnlyList<string> candidatePaths;

        if (string.Equals(request.PathScope, RepositorySearchPathScopes.Repository, StringComparison.Ordinal))
        {
            try
            {
                candidatePaths = await loadFileTreeAsync(branch, ct);
            }
            catch (Exception ex)
            {
                return new RepositorySearchResult(
                    RepositorySearchStatuses.Partial,
                    request.BranchSide,
                    request.PathScope,
                    fileMask,
                    [],
                    [new RepositorySearchLimitation(null, RepositorySearchLimitationReasons.ProviderFetchFailed, ex.Message)],
                    false);
            }

            candidatePaths = candidatePaths
                .Select(path => NormalizeRepositoryPath(path, normalizePath))
                .Where(path => MatchesFileMask(path, fileMask))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly();
        }
        else
        {
            candidatePaths = ResolveChangedScopeCandidates(request, changedPathSnapshots, fileMask, normalizePath, limitations);
        }

        var matches = new List<RepositorySearchMatch>();
        var truncated = false;

        foreach (var candidatePath in candidatePaths)
        {
            if (BinaryFileDetector.IsBinary(candidatePath))
            {
                limitations.Add(
                    new RepositorySearchLimitation(candidatePath, RepositorySearchLimitationReasons.BinaryFile, "Binary files are not searchable."));
                continue;
            }

            string? content;
            try
            {
                content = await fetchRawFileContentAsync(candidatePath, branch, ct);
            }
            catch (Exception ex)
            {
                limitations.Add(new RepositorySearchLimitation(candidatePath, RepositorySearchLimitationReasons.ProviderFetchFailed, ex.Message));
                continue;
            }

            if (content is null)
            {
                limitations.Add(
                    new RepositorySearchLimitation(
                        candidatePath, RepositorySearchLimitationReasons.MissingOnBranch, "The file was not found on the requested branch."));
                continue;
            }

            var byteSize = Encoding.UTF8.GetByteCount(content);
            if (byteSize > maxFileSizeBytes)
            {
                limitations.Add(
                    new RepositorySearchLimitation(
                        candidatePath,
                        RepositorySearchLimitationReasons.UnreadableFile,
                        $"The file is too large to search ({byteSize} bytes exceeds the limit of {maxFileSizeBytes} bytes)."));
                continue;
            }

            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i]))
                {
                    continue;
                }

                matches.Add(new RepositorySearchMatch(candidatePath, i + 1, lines[i].TrimEnd('\r')));
                if (matches.Count < MaxReturnedMatches)
                {
                    continue;
                }

                truncated = HasMoreMatches(regex, lines, i + 1) || HasMoreCandidates(candidatePaths, candidatePath);
                if (truncated)
                {
                    limitations.Add(
                        new RepositorySearchLimitation(
                            null,
                            RepositorySearchLimitationReasons.ResultTruncated,
                            $"Only the first {MaxReturnedMatches} matches were returned."));
                }

                goto Complete;
            }
        }

        Complete:
        var status = ResolveStatus(matches.Count, limitations.Count, truncated);
        return new RepositorySearchResult(
            status,
            request.BranchSide,
            request.PathScope,
            fileMask,
            matches.AsReadOnly(),
            limitations.AsReadOnly(),
            truncated);
    }

    private static IReadOnlyList<string> ResolveChangedScopeCandidates(
        RepositorySearchRequest request,
        IReadOnlyList<ChangedPathSnapshot>? changedPathSnapshots,
        string? fileMask,
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
            var candidatePath = string.Equals(request.BranchSide, RepositorySearchBranchSides.Target, StringComparison.Ordinal)
                ? snapshot.EffectiveTargetPath
                : snapshot.Path;
            var normalizedPath = NormalizeRepositoryPath(candidatePath, normalizePath);

            if (!MatchesFileMask(normalizedPath, fileMask))
            {
                continue;
            }

            var existsOnBranch = string.Equals(request.BranchSide, RepositorySearchBranchSides.Target, StringComparison.Ordinal)
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

    private static string? ResolveBranch(
        string branchSide,
        string sourceBranch,
        string? targetBranch,
        Func<string, string> normalizeBranch)
    {
        if (string.Equals(branchSide, RepositorySearchBranchSides.Target, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(targetBranch) ? null : normalizeBranch(targetBranch);
        }

        return normalizeBranch(sourceBranch);
    }

    private static string ResolveStatus(int matchCount, int limitationCount, bool truncated)
    {
        if (matchCount > 0)
        {
            return limitationCount > 0 || truncated
                ? RepositorySearchStatuses.Partial
                : RepositorySearchStatuses.Success;
        }

        return limitationCount > 0
            ? RepositorySearchStatuses.Partial
            : RepositorySearchStatuses.NoMatch;
    }

    private static string NormalizeRepositoryPath(string path, Func<string, string> normalizePath)
    {
        return normalizePath(path).Replace('\\', '/').TrimStart('/');
    }

    private static string? NormalizeFileMask(string? fileMask)
    {
        return string.IsNullOrWhiteSpace(fileMask) ? null : fileMask.Trim();
    }

    private static bool MatchesFileMask(string path, string? fileMask)
    {
        if (string.IsNullOrWhiteSpace(fileMask))
        {
            return true;
        }

        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddInclude(fileMask);
        return matcher.Match(path).HasMatches;
    }

    private static bool HasMoreMatches(Regex regex, IReadOnlyList<string> lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            if (regex.IsMatch(lines[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasMoreCandidates(IReadOnlyList<string> candidatePaths, string currentPath)
    {
        var index = -1;
        for (var i = 0; i < candidatePaths.Count; i++)
        {
            if (!string.Equals(candidatePaths[i], currentPath, StringComparison.Ordinal))
            {
                continue;
            }

            index = i;
            break;
        }

        return index >= 0 && index < candidatePaths.Count - 1;
    }
}
