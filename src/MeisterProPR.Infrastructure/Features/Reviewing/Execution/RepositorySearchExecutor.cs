// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.RegularExpressions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

internal static class RepositorySearchExecutor
{
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
            regex = ToolTimingCollectorContext.Record(
                ProtocolEventToolPhaseNames.RequestPreparation,
                "Request preparation",
                () => new Regex(searchTerm, RegexOptions.CultureInvariant));
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
        var candidateResolution = await RepositoryDiscoveryHelpers.ResolveCandidatePathsAsync(
            request.BranchSide,
            request.PathScope,
            new CodeSearchFilterSet(FileGlob: fileMask),
            sourceBranch,
            targetBranch,
            changedPathSnapshots,
            loadFileTreeAsync,
            normalizeBranch,
            normalizePath,
            ct);
        if (candidateResolution.Branch is null)
        {
            return new RepositorySearchResult(
                RepositorySearchStatuses.InvalidRequest,
                request.BranchSide,
                request.PathScope,
                fileMask,
                [],
                candidateResolution.Limitations,
                false);
        }

        var branch = candidateResolution.Branch;
        var candidatePaths = candidateResolution.Paths;
        var limitations = candidateResolution.Limitations.ToList();

        var matches = new List<RepositorySearchMatch>();
        var truncated = false;

        await ToolTimingCollectorContext.RecordAsync(
            ProtocolEventToolPhaseNames.RepositorySearch,
            "Repository search",
            async () =>
            {
                var scanned = 0;
                foreach (var candidatePath in candidatePaths)
                {
                    scanned++;
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
                    var searchTerminated = false;
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (!regex.IsMatch(lines[i]))
                        {
                            continue;
                        }

                        matches.Add(new RepositorySearchMatch(candidatePath, i + 1, lines[i].TrimEnd('\r')));
                        if (matches.Count < RepositoryDiscoveryHelpers.MaxReturnedMatches)
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
                                    $"Only the first {RepositoryDiscoveryHelpers.MaxReturnedMatches} matches were returned."));
                        }

                        searchTerminated = true;
                        break;
                    }

                    if (searchTerminated)
                    {
                        break;
                    }
                }

                return scanned;
            },
            scanned => $"files_scanned={scanned};matches={matches.Count};truncated={truncated}");

        var status = ToolTimingCollectorContext.Record(
            ProtocolEventToolPhaseNames.ResultShaping,
            "Result shaping",
            () => ResolveStatus(matches.Count, limitations.Count, truncated),
            resolved => $"matches={matches.Count};limitations={limitations.Count};status={resolved}");
        return new RepositorySearchResult(
            status,
            request.BranchSide,
            request.PathScope,
            fileMask,
            matches.AsReadOnly(),
            limitations.AsReadOnly(),
            truncated,
            ToolTimingCollectorContext.CaptureSnapshot());
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

    private static string? NormalizeFileMask(string? fileMask)
    {
        return string.IsNullOrWhiteSpace(fileMask) ? null : fileMask.Trim();
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
