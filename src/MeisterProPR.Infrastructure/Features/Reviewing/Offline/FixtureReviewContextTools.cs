// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Text;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.CodeAnalysis;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Fixture-backed implementation of reviewer-facing repository tools.
/// </summary>
public sealed class FixtureReviewContextTools(
    ReviewEvaluationFixture fixture,
    IOptions<AiReviewOptions> options,
    IProCursorGateway proCursorGateway,
    Guid? clientId,
    IReadOnlyList<Guid>? knowledgeSourceIds,
    IStructuralCodeAnalyzer? structuralAnalyzer = null) : IReviewContextTools, IProCursorAvailabilityAware
{
    private readonly ConcurrentDictionary<string, string> _fileCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FileNeighborhood> _fileNeighborhoodCache = new(StringComparer.Ordinal);
    private readonly AiReviewOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, RepositoryOverview> _repositoryOverviewCache = new(StringComparer.Ordinal);

    public bool SupportsProCursorTools => proCursorGateway is not DisabledProCursorGateway;

    public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<ChangedFileSummary>>(
            fixture.PullRequestSnapshot.ChangedFiles
                .Select(file => new ChangedFileSummary(file.Path, file.ChangeType))
                .ToList()
                .AsReadOnly());
    }

    public Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<string>>(
            this.ResolveFilesForBranch(branch)
                .Select(file => file.Path)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList()
                .AsReadOnly());
    }

    public Task<string> GetFileContentAsync(string path, string branch, int startLine, int endLine, CancellationToken ct)
    {
        var normalizedPath = path.Trim();
        if (BinaryFileDetector.IsBinary(normalizedPath))
        {
            return Task.FromResult($"[Binary file — content not available: {normalizedPath}]");
        }

        if (!this._fileCache.TryGetValue(normalizedPath, out var content))
        {
            var cacheKey = $"{NormalizeBranch(branch)}:{normalizedPath}";
            if (this._fileCache.TryGetValue(cacheKey, out content))
            {
                return SliceContent(content, startLine, endLine);
            }

            var entry = this.ResolveFilesForBranch(branch).FirstOrDefault(file => string.Equals(file.Path, normalizedPath, StringComparison.Ordinal));
            if (entry is null || entry.IsBinary)
            {
                return Task.FromResult(string.Empty);
            }

            var byteSize = Encoding.UTF8.GetByteCount(entry.Content);
            if (byteSize > this._options.MaxFileSizeBytes)
            {
                return Task.FromResult($"[File too large: {byteSize} bytes exceeds limit of {this._options.MaxFileSizeBytes} bytes]");
            }

            content = entry.Content;
            this._fileCache[cacheKey] = content;
        }

        return SliceContent(content, startLine, endLine);
    }

    public Task<RepositorySearchResult> SearchSourceRepoAsync(string searchTerm, string? fileMask, CancellationToken ct)
    {
        return this.SearchAsync(
            new RepositorySearchRequest(searchTerm, fileMask, RepositorySearchBranchSides.Source, RepositorySearchPathScopes.Repository),
            ct);
    }

    public Task<RepositorySearchResult> SearchSourceChangedFilesAsync(string searchTerm, string? fileMask, CancellationToken ct)
    {
        return this.SearchAsync(
            new RepositorySearchRequest(searchTerm, fileMask, RepositorySearchBranchSides.Source, RepositorySearchPathScopes.ChangedFiles),
            ct);
    }

    public Task<RepositorySearchResult> SearchTargetRepoAsync(string searchTerm, string? fileMask, CancellationToken ct)
    {
        return this.SearchAsync(
            new RepositorySearchRequest(searchTerm, fileMask, RepositorySearchBranchSides.Target, RepositorySearchPathScopes.Repository),
            ct);
    }

    public Task<RepositorySearchResult> SearchTargetChangedFilesAsync(string searchTerm, string? fileMask, CancellationToken ct)
    {
        return this.SearchAsync(
            new RepositorySearchRequest(searchTerm, fileMask, RepositorySearchBranchSides.Target, RepositorySearchPathScopes.ChangedFiles),
            ct);
    }

    public Task<CodeSearchResult> SearchCodeAsync(CodeSearchRequest request, CancellationToken ct)
    {
        return RepositoryCodeSearchExecutor.ExecuteAsync(
            request,
            fixture.PullRequestSnapshot.SourceBranch,
            fixture.PullRequestSnapshot.TargetBranch,
            fixture.PullRequestSnapshot.ChangedFiles.Select(ChangedPathSnapshot.FromFixtureChangedFile).ToList().AsReadOnly(),
            (branch, _) => Task.FromResult<IReadOnlyList<string>>(
                this.ResolveFilesForBranch(branch)
                    .Select(file => file.Path)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList()
                    .AsReadOnly()),
            (path, branch, _) => Task.FromResult<string?>(
                this.ResolveFilesForBranch(branch)
                    .FirstOrDefault(file => string.Equals(file.Path, path.Trim(), StringComparison.Ordinal))?
                    .Content),
            NormalizeBranch,
            path => path.Trim(),
            this._options.MaxFileSizeBytes,
            ct);
    }

    public Task<PathSearchResult> SearchPathsAsync(PathSearchRequest request, CancellationToken ct)
    {
        return RepositoryPathSearchExecutor.ExecuteAsync(
            request,
            fixture.PullRequestSnapshot.SourceBranch,
            fixture.PullRequestSnapshot.TargetBranch,
            fixture.PullRequestSnapshot.ChangedFiles.Select(ChangedPathSnapshot.FromFixtureChangedFile).ToList().AsReadOnly(),
            (branch, _) => Task.FromResult<IReadOnlyList<string>>(
                this.ResolveFilesForBranch(branch)
                    .Select(file => file.Path)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList()
                    .AsReadOnly()),
            NormalizeBranch,
            path => path.Trim(),
            ct);
    }

    public Task<RepositoryOverview> GetRepositoryOverviewAsync(string branchSide, CancellationToken ct)
    {
        var normalizedBranchSide = NormalizeBranchSide(branchSide);
        if (this._repositoryOverviewCache.TryGetValue(normalizedBranchSide, out var cached))
        {
            return Task.FromResult(cached);
        }

        var branch = RepositoryDiscoveryHelpers.ResolveBranch(
            normalizedBranchSide,
            fixture.PullRequestSnapshot.SourceBranch,
            fixture.PullRequestSnapshot.TargetBranch,
            NormalizeBranch);
        if (branch is null)
        {
            return Task.FromResult(RepositoryOverview.CreateBlocked(normalizedBranchSide, RepositorySearchStatuses.InvalidRequest));
        }

        var overview = RepositoryOverviewBuilder.Build(
            normalizedBranchSide,
            branch,
            this.ResolveFilesForBranch(branch).Select(file => file.Path).ToList().AsReadOnly());
        this._repositoryOverviewCache[normalizedBranchSide] = overview;
        return Task.FromResult(overview);
    }

    public Task<FileNeighborhood> GetFileNeighborhoodAsync(string filePath, string branchSide, CancellationToken ct)
    {
        var normalizedBranchSide = NormalizeBranchSide(branchSide);
        var normalizedPath = filePath.Trim().TrimStart('/');
        var cacheKey = $"{normalizedBranchSide}:{normalizedPath}";
        if (this._fileNeighborhoodCache.TryGetValue(cacheKey, out var cached))
        {
            return Task.FromResult(cached);
        }

        var branch = RepositoryDiscoveryHelpers.ResolveBranch(
            normalizedBranchSide,
            fixture.PullRequestSnapshot.SourceBranch,
            fixture.PullRequestSnapshot.TargetBranch,
            NormalizeBranch);
        if (branch is null)
        {
            return Task.FromResult(FileNeighborhood.CreateBlocked(normalizedBranchSide, normalizedPath, RepositorySearchStatuses.InvalidRequest));
        }

        var neighborhood = FileNeighborhoodBuilder.Build(
            normalizedBranchSide,
            branch,
            normalizedPath,
            this.ResolveFilesForBranch(branch).Select(file => file.Path).ToList().AsReadOnly());
        this._fileNeighborhoodCache[cacheKey] = neighborhood;
        return Task.FromResult(neighborhood);
    }

    public async Task<ProCursorKnowledgeAnswerDto> AskProCursorKnowledgeAsync(string question, CancellationToken ct)
    {
        if (!clientId.HasValue)
        {
            return new ProCursorKnowledgeAnswerDto(
                "unavailable",
                [],
                "The current review context does not include a client identifier for ProCursor.");
        }

        try
        {
            return await proCursorGateway.AskKnowledgeAsync(
                new ProCursorKnowledgeQueryRequest(
                    clientId.Value,
                    question,
                    knowledgeSourceIds,
                    new ProCursorRepositoryContextDto(
                        fixture.PullRequestSnapshot.CodeReview.Repository.Host.HostBaseUrl,
                        fixture.PullRequestSnapshot.CodeReview.Repository.OwnerOrNamespace,
                        fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
                        fixture.PullRequestSnapshot.SourceBranch)),
                ct);
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            return new ProCursorKnowledgeAnswerDto("unavailable", [], ex.Message);
        }
    }

    public async Task<ProCursorSymbolInsightDto> GetProCursorSymbolInfoAsync(
        string symbol,
        string? queryMode,
        int? maxRelations,
        CancellationToken ct)
    {
        if (!clientId.HasValue)
        {
            return new ProCursorSymbolInsightDto(
                "unavailable",
                null,
                false,
                false,
                null,
                []);
        }

        try
        {
            return await proCursorGateway.GetSymbolInsightAsync(
                new ProCursorSymbolQueryRequest(
                    clientId.Value,
                    symbol,
                    string.IsNullOrWhiteSpace(queryMode) ? "name" : queryMode.Trim(),
                    StateMode: "reviewTarget",
                    ReviewContext: new ProCursorReviewContextDto(
                        fixture.PullRequestSnapshot.CodeReview.Repository.ExternalRepositoryId,
                        fixture.PullRequestSnapshot.SourceBranch,
                        fixture.PullRequestSnapshot.CodeReview.Number,
                        1),
                    MaxRelations: maxRelations),
                ct);
        }
        catch (ProCursorDependencyUnavailableException)
        {
            return new ProCursorSymbolInsightDto("unavailable", null, false, false, null, []);
        }
    }

    /// <inheritdoc />
    public async Task<ReferenceLookupResult> FindReferencesAsync(SymbolReferenceQuery query, CancellationToken ct)
    {
        if (query is null || string.IsNullOrWhiteSpace(query.Symbol))
        {
            return ReferenceLookupResult.Empty;
        }

        if (structuralAnalyzer is null || !this._options.EnableStructuralReferenceTools)
        {
            return ReferenceLookupResult.UnavailableResult;
        }

        var sites = new List<ReferenceSite>();
        var scanned = 0;
        var truncated = false;
        var usedChars = 0;

        foreach (var file in this.ResolveFilesForBranch(this.ResolveBranchName(query.BranchSide)))
        {
            if (scanned >= this._options.MaxReferenceCandidateFiles)
            {
                truncated = true;
                break;
            }

            if (!structuralAnalyzer.CanAnalyze(file.Path) || LanguagePaths.TryResolve(file.Path) is not { } language
                                                          || string.IsNullOrEmpty(file.Content))
            {
                continue;
            }

            scanned++;
            bool fileTruncated;
            (fileTruncated, usedChars) = await this.AppendReferenceSitesForFileAsync(file, language, query.Symbol, sites, usedChars, ct);
            if (fileTruncated)
            {
                return new ReferenceLookupResult(sites, scanned, true, false);
            }
        }

        return new ReferenceLookupResult(sites, scanned, truncated, false);
    }

    /// <inheritdoc />
    public async Task<DefinitionLookupResult> GetDefinitionAsync(SymbolReferenceQuery query, CancellationToken ct)
    {
        if (query is null || string.IsNullOrWhiteSpace(query.Symbol))
        {
            return DefinitionLookupResult.Empty;
        }

        if (structuralAnalyzer is null || !this._options.EnableStructuralReferenceTools)
        {
            return DefinitionLookupResult.UnavailableResult;
        }

        var definitions = new List<DefinitionLookupSite>();
        var scanned = 0;
        var truncated = false;

        foreach (var file in this.ResolveFilesForBranch(this.ResolveBranchName(query.BranchSide)))
        {
            if (scanned >= this._options.MaxReferenceCandidateFiles)
            {
                truncated = true;
                break;
            }

            if (!structuralAnalyzer.CanAnalyze(file.Path) || LanguagePaths.TryResolve(file.Path) is not { } language
                                                          || string.IsNullOrEmpty(file.Content))
            {
                continue;
            }

            scanned++;
            var request = new StructuralParseRequest(file.Path, language, file.Content, []);
            var defs = await structuralAnalyzer.GetDefinitionsAsync(request, ct);
            var contentLines = ReferenceSnippetEnricher.SplitLines(file.Content);

            foreach (var def in defs.Where(d => string.Equals(d.Name, query.Symbol, StringComparison.Ordinal)))
            {
                if (definitions.Count >= this._options.MaxReferenceResults)
                {
                    return new DefinitionLookupResult(definitions, scanned, true, false);
                }

                var snippet = ReferenceSnippetEnricher.ExtractSnippet(contentLines, def.StartLine);
                definitions.Add(new DefinitionLookupSite(file.Path, def.Kind, def.Name, def.StartLine, def.EndLine, ResolutionMode.NameBased, snippet));
            }
        }

        return new DefinitionLookupResult(definitions, scanned, truncated, false);
    }

    private async Task<(bool Truncated, int UsedChars)> AppendReferenceSitesForFileAsync(
        RepositoryFileEntry file,
        SupportedLanguage language,
        string symbol,
        List<ReferenceSite> sites,
        int usedChars,
        CancellationToken ct)
    {
        var request = new StructuralParseRequest(file.Path, language, file.Content, []);
        var lines = await structuralAnalyzer!.ConfirmReferenceLinesAsync(request, symbol, ct);

        // The file content is already in hand, so the matched-line snippet and enclosing symbol are free:
        // extract them here rather than forcing a follow-up fetch of every site.
        var contentLines = ReferenceSnippetEnricher.SplitLines(file.Content);
        var enclosingByLine = await ReferenceSnippetEnricher.ResolveEnclosingByLineAsync(
            structuralAnalyzer,
            file.Path,
            language,
            file.Content,
            lines,
            ct);

        foreach (var line in lines)
        {
            if (sites.Count >= this._options.MaxReferenceResults || usedChars > this._options.MaxReferenceResultChars)
            {
                return (true, usedChars);
            }

            var snippet = ReferenceSnippetEnricher.ExtractSnippet(contentLines, line);
            enclosingByLine.TryGetValue(line, out var enclosing);
            sites.Add(
                new ReferenceSite(
                    file.Path,
                    line,
                    enclosing?.Name,
                    enclosing?.Kind,
                    OccurrenceKind.Reference,
                    ResolutionMode.NameBased,
                    snippet));
            usedChars += file.Path.Length + 16 + snippet.Length;
        }

        // The last added site may itself have pushed the running total past the cap; report truncation
        // when we ended over the limit so callers do not treat an over-budget result as complete.
        return (usedChars > this._options.MaxReferenceResultChars, usedChars);
    }

    private string ResolveBranchName(string branchSide)
    {
        // Map the logical side token (source/target) to the fixture's actual branch name so
        // ResolveFilesForBranch selects the right in-memory file set.
        return string.Equals(branchSide, RepositorySearchBranchSides.Target, StringComparison.OrdinalIgnoreCase)
            ? fixture.RepositorySnapshot.TargetBranch
            : fixture.RepositorySnapshot.SourceBranch;
    }

    private Task<RepositorySearchResult> SearchAsync(RepositorySearchRequest request, CancellationToken ct)
    {
        return RepositorySearchExecutor.ExecuteAsync(
            request,
            fixture.PullRequestSnapshot.SourceBranch,
            fixture.PullRequestSnapshot.TargetBranch,
            fixture.PullRequestSnapshot.ChangedFiles.Select(ChangedPathSnapshot.FromFixtureChangedFile).ToList().AsReadOnly(),
            (branch, _) => Task.FromResult<IReadOnlyList<string>>(
                this.ResolveFilesForBranch(branch)
                    .Select(file => file.Path)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList()
                    .AsReadOnly()),
            (path, branch, _) => Task.FromResult<string?>(
                this.ResolveFilesForBranch(branch)
                    .FirstOrDefault(file => string.Equals(file.Path, path.Trim(), StringComparison.Ordinal))?
                    .Content),
            NormalizeBranch,
            path => path.Trim(),
            this._options.MaxFileSizeBytes,
            ct);
    }

    private static string NormalizeBranch(string branch)
    {
        return branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch.Trim();
    }

    private static string NormalizeBranchSide(string branchSide)
    {
        return string.Equals(branchSide, RepositorySearchBranchSides.Target, StringComparison.OrdinalIgnoreCase)
            ? RepositorySearchBranchSides.Target
            : RepositorySearchBranchSides.Source;
    }

    private IReadOnlyList<RepositoryFileEntry> ResolveFilesForBranch(string branch)
    {
        var normalized = NormalizeBranch(branch);
        var sourceBranch = NormalizeBranch(fixture.RepositorySnapshot.SourceBranch);
        var targetBranch = NormalizeBranch(fixture.RepositorySnapshot.TargetBranch);

        return string.Equals(normalized, targetBranch, StringComparison.OrdinalIgnoreCase)
            ? fixture.RepositorySnapshot.TargetFilesOrSource
            : fixture.RepositorySnapshot.SourceFiles;
    }

    private static Task<string> SliceContent(string? content, int startLine, int endLine)
    {
        if (string.IsNullOrEmpty(content))
        {
            return Task.FromResult(string.Empty);
        }

        var lines = content.Split('\n');
        var clampedStart = Math.Max(1, startLine);
        var clampedEnd = Math.Min(lines.Length, endLine);
        return Task.FromResult(clampedStart > clampedEnd ? string.Empty : string.Join("\n", lines[(clampedStart - 1)..clampedEnd]));
    }
}
