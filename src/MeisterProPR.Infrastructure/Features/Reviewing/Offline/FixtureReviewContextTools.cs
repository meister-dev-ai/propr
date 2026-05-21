// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Text;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
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
    IReadOnlyList<Guid>? knowledgeSourceIds) : IReviewContextTools, IProCursorAvailabilityAware
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
