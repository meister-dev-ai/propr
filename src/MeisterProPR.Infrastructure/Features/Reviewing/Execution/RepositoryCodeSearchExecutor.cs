// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.RegularExpressions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.CodeAnalysis;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

internal static class RepositoryCodeSearchExecutor
{
    private const int MaxPreviewLength = 240;

    public static async Task<CodeSearchResult> ExecuteAsync(
        CodeSearchRequest request,
        string sourceBranch,
        string? targetBranch,
        IReadOnlyList<ChangedPathSnapshot>? changedPathSnapshots,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> loadFileTreeAsync,
        Func<string, string, CancellationToken, Task<string?>> fetchRawFileContentAsync,
        Func<string, string> normalizeBranch,
        Func<string, string> normalizePath,
        int maxFileSizeBytes,
        CancellationToken ct,
        IStructuralCodeAnalyzer? structuralAnalyzer = null,
        bool confirmRelatedSymbolStructurally = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(loadFileTreeAsync);
        ArgumentNullException.ThrowIfNull(fetchRawFileContentAsync);
        ArgumentNullException.ThrowIfNull(normalizeBranch);
        ArgumentNullException.ThrowIfNull(normalizePath);

        var queryText = request.QueryText?.Trim() ?? string.Empty;
        var searchMode = NormalizeSearchMode(request.SearchMode);
        var filters = RepositoryDiscoveryHelpers.NormalizeFilters(request.Filters);
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return InvalidRequest(request, searchMode, filters, "Search query text is required.");
        }

        var regexOutcome = await TryBuildSearchRegexAsync(queryText, searchMode, ct);
        if (regexOutcome.InvalidRequest is not null)
        {
            return InvalidRequest(request, searchMode, filters, regexOutcome.InvalidRequest);
        }

        if (regexOutcome.UnsupportedMode)
        {
            return new CodeSearchResult(
                RepositorySearchStatuses.InvalidRequest,
                request.BranchSide,
                request.PathScope,
                searchMode,
                filters,
                [],
                [
                    new RepositorySearchLimitation(
                        null, RepositorySearchLimitationReasons.UnsupportedSearchMode, $"Unsupported code search mode '{request.SearchMode}'."),
                ],
                false);
        }

        var candidateResolution = await RepositoryDiscoveryHelpers.ResolveCandidatePathsAsync(
            request.BranchSide,
            request.PathScope,
            filters,
            new RepositoryAccessContext(sourceBranch, targetBranch, changedPathSnapshots, loadFileTreeAsync, normalizeBranch, normalizePath),
            ct);

        if (candidateResolution.Branch is null)
        {
            return new CodeSearchResult(
                RepositorySearchStatuses.InvalidRequest,
                request.BranchSide,
                request.PathScope,
                searchMode,
                filters,
                [],
                candidateResolution.Limitations,
                false);
        }

        var state = new SearchState(candidateResolution.Paths, candidateResolution.Limitations.ToList());
        await ScanAllCandidatesAsync(
            state,
            searchMode,
            queryText,
            regexOutcome.Regex,
            candidateResolution.Branch,
            maxFileSizeBytes,
            confirmRelatedSymbolStructurally,
            structuralAnalyzer,
            fetchRawFileContentAsync,
            ct);

        return new CodeSearchResult(
            ResolveStatus(state.Matches.Count, state.Limitations.Count, state.Truncated),
            request.BranchSide,
            request.PathScope,
            searchMode,
            filters,
            state.Matches.AsReadOnly(),
            state.Limitations.AsReadOnly(),
            state.Truncated,
            ToolTimingCollectorContext.CaptureSnapshot());
    }

    private static async Task<RegexBuildOutcome> TryBuildSearchRegexAsync(
        string queryText,
        string searchMode,
        CancellationToken ct)
    {
        if (string.Equals(searchMode, CodeSearchModes.Regex, StringComparison.Ordinal))
        {
            try
            {
                var regex = ToolTimingCollectorContext.Record(
                    ProtocolEventToolPhaseNames.RequestPreparation,
                    "Request preparation",
                    () => new Regex(queryText, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
                return new RegexBuildOutcome(regex, null, false);
            }
            catch (ArgumentException ex)
            {
                return new RegexBuildOutcome(null, ex.Message, false);
            }
        }

        _ = ct;
        return new RegexBuildOutcome(null, null, !IsSupportedMode(searchMode));
    }

    private static async Task ScanAllCandidatesAsync(
        SearchState state,
        string searchMode,
        string queryText,
        Regex? regex,
        string branch,
        int maxFileSizeBytes,
        bool confirmRelatedSymbolStructurally,
        IStructuralCodeAnalyzer? structuralAnalyzer,
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
                    var outcome = await ScanSingleCandidateAsync(
                        candidatePath,
                        branch,
                        searchMode,
                        queryText,
                        regex,
                        maxFileSizeBytes,
                        confirmRelatedSymbolStructurally,
                        structuralAnalyzer,
                        fetchRawFileContentAsync,
                        state,
                        ct);
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
        string searchMode,
        string queryText,
        Regex? regex,
        int maxFileSizeBytes,
        bool confirmRelatedSymbolStructurally,
        IStructuralCodeAnalyzer? structuralAnalyzer,
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

        var language = RepositoryDiscoveryHelpers.InferLanguage(candidatePath);
        var lines = content.Split('\n');

        var confirmedLines = await TryConfirmLinesStructurallyAsync(
            candidatePath,
            content,
            queryText,
            searchMode,
            confirmRelatedSymbolStructurally,
            structuralAnalyzer,
            ct);

        return ScanFileContentForMatches(
            candidatePath,
            lines,
            queryText,
            searchMode,
            regex,
            language,
            confirmedLines,
            state);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation is not a per-file provider failure; let it unwind the scan.
            throw;
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
                    candidatePath,
                    RepositorySearchLimitationReasons.MissingOnBranch,
                    "The file was not found on the requested branch."));
            return null;
        }

        return content;
    }

    private static async Task<HashSet<int>?> TryConfirmLinesStructurallyAsync(
        string candidatePath,
        string content,
        string queryText,
        string searchMode,
        bool confirmRelatedSymbolStructurally,
        IStructuralCodeAnalyzer? structuralAnalyzer,
        CancellationToken ct)
    {
        // For related_symbol, post-filter substring matches through the structural backend so
        // comment/string occurrences are dropped. Kill-switch off (or an unsupported language /
        // unavailable backend) → today's substring behavior (no regression).
        if (!confirmRelatedSymbolStructurally
            || structuralAnalyzer is null
            || !string.Equals(searchMode, CodeSearchModes.RelatedSymbol, StringComparison.Ordinal)
            || !structuralAnalyzer.CanAnalyze(candidatePath)
            || LanguagePaths.TryResolve(candidatePath) is not { } structuralLanguage)
        {
            return null;
        }

        try
        {
            var structuralRequest = new StructuralParseRequest(candidatePath, structuralLanguage, content, []);
            var confirmed = await structuralAnalyzer.ConfirmReferenceLinesAsync(structuralRequest, queryText, ct);
            return confirmed.Count == 0 ? new HashSet<int>() : [.. confirmed];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // fail-soft to substring behavior
        }
    }

    private static CandidateScanOutcome ScanFileContentForMatches(
        string candidatePath,
        string[] lines,
        string queryText,
        string searchMode,
        Regex? regex,
        string language,
        HashSet<int>? confirmedLines,
        SearchState state)
    {
        try
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (!LineMatches(line, queryText, searchMode, regex))
                {
                    continue;
                }

                // Structural confirmation: drop matches the backend did not confirm
                // as a real identifier/reference line (comment/string occurrences).
                if (confirmedLines is not null && !confirmedLines.Contains(i + 1))
                {
                    continue;
                }

                state.Matches.Add(
                    new CodeSearchMatch(
                        candidatePath,
                        i + 1,
                        Truncate(line.Trim(), out var previewTruncated),
                        language,
                        state.Matches.Count + 1,
                        previewTruncated));
                if (state.Matches.Count < RepositoryDiscoveryHelpers.MaxReturnedMatches)
                {
                    continue;
                }

                state.Truncated = HasMoreMatches(lines, i + 1, queryText, searchMode, regex) ||
                                  HasMoreCandidates(state.CandidatePaths, candidatePath);
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
        public List<CodeSearchMatch> Matches { get; } = [];
        public List<RepositorySearchLimitation> Limitations { get; }
        public bool Truncated;
    }

    private enum CandidateScanOutcome
    {
        Continue,
        Terminated,
    }

    private sealed record RegexBuildOutcome(Regex? Regex, string? InvalidRequest, bool UnsupportedMode);

    private static CodeSearchResult InvalidRequest(
        CodeSearchRequest request,
        string searchMode,
        CodeSearchFilterSet? filters,
        string message)
    {
        return new CodeSearchResult(
            RepositorySearchStatuses.InvalidRequest,
            request.BranchSide,
            request.PathScope,
            searchMode,
            filters,
            [],
            [new RepositorySearchLimitation(null, RepositorySearchLimitationReasons.InvalidRegex, message)],
            false);
    }

    private static string NormalizeSearchMode(string? searchMode)
    {
        return string.IsNullOrWhiteSpace(searchMode)
            ? CodeSearchModes.Regex
            : searchMode.Trim().ToLowerInvariant();
    }

    private static bool IsSupportedMode(string searchMode)
    {
        return searchMode is CodeSearchModes.ExactIdentifier or CodeSearchModes.ExactPhrase or CodeSearchModes.Regex or
            CodeSearchModes.RelatedSymbol or CodeSearchModes.RelatedConfigKey or CodeSearchModes.RelatedRoute or
            CodeSearchModes.RelatedDependencyRegistration or CodeSearchModes.RelatedExceptionOrLog;
    }

    private static bool LineMatches(string line, string queryText, string searchMode, Regex? regex)
    {
        return searchMode switch
        {
            CodeSearchModes.Regex => regex?.IsMatch(line) == true,
            CodeSearchModes.ExactIdentifier => ContainsExactIdentifier(line, queryText),
            CodeSearchModes.ExactPhrase => line.Contains(queryText, StringComparison.Ordinal),
            _ => line.Contains(queryText, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static bool ContainsExactIdentifier(string line, string identifier)
    {
        var start = 0;
        while (start < line.Length)
        {
            var index = line.IndexOf(identifier, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var beforeOk = index == 0 || !IsIdentifierCharacter(line[index - 1]);
            var afterIndex = index + identifier.Length;
            var afterOk = afterIndex >= line.Length || !IsIdentifierCharacter(line[afterIndex]);
            if (beforeOk && afterOk)
            {
                return true;
            }

            start = index + identifier.Length;
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch == '_';
    }

    private static string Truncate(string value, out bool truncated)
    {
        if (value.Length <= MaxPreviewLength)
        {
            truncated = false;
            return value;
        }

        truncated = true;
        return value[..MaxPreviewLength];
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

    private static bool HasMoreMatches(IReadOnlyList<string> lines, int startIndex, string queryText, string searchMode, Regex? regex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            if (LineMatches(lines[i], queryText, searchMode, regex))
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
