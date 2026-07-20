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

        if (string.IsNullOrWhiteSpace(request.SearchTerm?.Trim() ?? string.Empty))
        {
            return BuildInvalidRequestResult(request, "Search term is required.");
        }

        if (!TryBuildSearchRegex(request.SearchTerm!.Trim(), out var regex, out var regexError))
        {
            return BuildInvalidRequestResult(request, regexError);
        }

        var fileMask = NormalizeFileMask(request.FileMask);
        var candidateResolution = await RepositoryDiscoveryHelpers.ResolveCandidatePathsAsync(
            request.BranchSide,
            request.PathScope,
            new CodeSearchFilterSet(FileGlob: fileMask),
            new RepositoryAccessContext(sourceBranch, targetBranch, changedPathSnapshots, loadFileTreeAsync, normalizeBranch, normalizePath),
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

        var state = new SearchState(candidateResolution.Paths, candidateResolution.Limitations.ToList());
        await ScanAllCandidatesAsync(
            state,
            regex,
            candidateResolution.Branch,
            maxFileSizeBytes,
            fetchRawFileContentAsync,
            ct);

        var status = ToolTimingCollectorContext.Record(
            ProtocolEventToolPhaseNames.ResultShaping,
            "Result shaping",
            () => ResolveStatus(state.Matches.Count, state.Limitations.Count, state.Truncated),
            resolved => $"matches={state.Matches.Count};limitations={state.Limitations.Count};status={resolved}");
        return new RepositorySearchResult(
            status,
            request.BranchSide,
            request.PathScope,
            fileMask,
            state.Matches.AsReadOnly(),
            state.Limitations.AsReadOnly(),
            state.Truncated,
            ToolTimingCollectorContext.CaptureSnapshot());
    }

    private static RepositorySearchResult BuildInvalidRequestResult(RepositorySearchRequest request, string message)
    {
        return new RepositorySearchResult(
            RepositorySearchStatuses.InvalidRequest,
            request.BranchSide,
            request.PathScope,
            NormalizeFileMask(request.FileMask),
            [],
            [new RepositorySearchLimitation(null, RepositorySearchLimitationReasons.InvalidRegex, message)],
            false);
    }

    private static bool TryBuildSearchRegex(string searchTerm, out Regex regex, out string error)
    {
        try
        {
            regex = ToolTimingCollectorContext.Record(
                ProtocolEventToolPhaseNames.RequestPreparation,
                "Request preparation",
                () => new Regex(searchTerm, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            error = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            regex = null!;
            error = ex.Message;
            return false;
        }
    }

    private static async Task ScanAllCandidatesAsync(
        SearchState state,
        Regex regex,
        string branch,
        int maxFileSizeBytes,
        Func<string, string, CancellationToken, Task<string?>> fetchRawFileContentAsync,
        CancellationToken ct)
    {
        await ToolTimingCollectorContext.RecordAsync(
            ProtocolEventToolPhaseNames.RepositorySearch,
            "Repository search",
            async () =>
            {
                var scanned = 0;
                foreach (var candidatePath in state.CandidatePaths)
                {
                    scanned++;
                    var outcome = await ScanSingleCandidateAsync(candidatePath, branch, maxFileSizeBytes, regex, fetchRawFileContentAsync, state, ct);
                    if (outcome == CandidateScanOutcome.Terminated)
                    {
                        break;
                    }
                }

                return scanned;
            },
            scanned => $"files_scanned={scanned};matches={state.Matches.Count};truncated={state.Truncated}");
    }

    private static async Task<CandidateScanOutcome> ScanSingleCandidateAsync(
        string candidatePath,
        string branch,
        int maxFileSizeBytes,
        Regex regex,
        Func<string, string, CancellationToken, Task<string?>> fetchRawFileContentAsync,
        SearchState state,
        CancellationToken ct)
    {
        if (BinaryFileDetector.IsBinary(candidatePath))
        {
            state.Limitations.Add(
                new RepositorySearchLimitation(candidatePath, RepositorySearchLimitationReasons.BinaryFile, "Binary files are not searchable."));
            return CandidateScanOutcome.Continue;
        }

        var content = await TryFetchFileContentAsync(candidatePath, branch, fetchRawFileContentAsync, state, ct);
        if (content is null)
        {
            return CandidateScanOutcome.Continue;
        }

        if (Encoding.UTF8.GetByteCount(content) > maxFileSizeBytes)
        {
            var byteSize = Encoding.UTF8.GetByteCount(content);
            state.Limitations.Add(
                new RepositorySearchLimitation(
                    candidatePath,
                    RepositorySearchLimitationReasons.UnreadableFile,
                    $"The file is too large to search ({byteSize} bytes exceeds the limit of {maxFileSizeBytes} bytes)."));
            return CandidateScanOutcome.Continue;
        }

        return ScanFileContentForMatches(candidatePath, content, regex, state);
    }

    private static async Task<string?> TryFetchFileContentAsync(
        string candidatePath,
        string branch,
        Func<string, string, CancellationToken, Task<string?>> fetchRawFileContentAsync,
        SearchState state,
        CancellationToken ct)
    {
        string? content;
        try
        {
            content = await fetchRawFileContentAsync(candidatePath, branch, ct);
        }
        catch (Exception ex)
        {
            state.Limitations.Add(new RepositorySearchLimitation(candidatePath, RepositorySearchLimitationReasons.ProviderFetchFailed, ex.Message));
            return null;
        }

        if (content is null)
        {
            state.Limitations.Add(
                new RepositorySearchLimitation(
                    candidatePath, RepositorySearchLimitationReasons.MissingOnBranch, "The file was not found on the requested branch."));
            return null;
        }

        return content;
    }

    private static CandidateScanOutcome ScanFileContentForMatches(
        string candidatePath,
        string content,
        Regex regex,
        SearchState state)
    {
        var lines = content.Split('\n');
        try
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (!regex.IsMatch(lines[i]))
                {
                    continue;
                }

                state.Matches.Add(new RepositorySearchMatch(candidatePath, i + 1, lines[i].TrimEnd('\r')));
                if (state.Matches.Count >= RepositoryDiscoveryHelpers.MaxReturnedMatches)
                {
                    state.Truncated = HasMoreMatches(regex, lines, i + 1) || HasMoreCandidates(state.CandidatePaths, candidatePath);
                    if (state.Truncated)
                    {
                        state.Limitations.Add(
                            new RepositorySearchLimitation(
                                null,
                                RepositorySearchLimitationReasons.ResultTruncated,
                                $"Only the first {RepositoryDiscoveryHelpers.MaxReturnedMatches} matches were returned."));
                    }

                    return CandidateScanOutcome.Terminated;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // The pattern is pathologically expensive against this file's content; retrying it
            // against the remaining candidates would just repeat the same timeout for each one.
            state.Limitations.Add(
                new RepositorySearchLimitation(
                    candidatePath,
                    RepositorySearchLimitationReasons.RegexTimedOut,
                    "The search pattern took too long to match and was aborted; try a simpler pattern."));
            return CandidateScanOutcome.Terminated;
        }

        return CandidateScanOutcome.Continue;
    }

    private sealed class SearchState
    {
        public SearchState(IReadOnlyList<string> candidatePaths, List<RepositorySearchLimitation> limitations)
        {
            this.CandidatePaths = candidatePaths;
            this.Limitations = limitations;
        }

        public IReadOnlyList<string> CandidatePaths { get; }
        public List<RepositorySearchMatch> Matches { get; } = [];
        public List<RepositorySearchLimitation> Limitations { get; }
        public bool Truncated;
    }

    private enum CandidateScanOutcome
    {
        Continue,
        Terminated,
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
