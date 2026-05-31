// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Text;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

internal abstract class ProviderReviewContextToolsBase(
    IProCursorGateway proCursorGateway,
    IOptions<AiReviewOptions> options,
    CodeReviewRef review,
    string sourceBranch,
    int iterationId,
    Guid? clientId,
    IReadOnlyList<Guid>? knowledgeSourceIds,
    ILogger logger,
    string? providerScopePath = null,
    string? targetBranch = null,
    IReadOnlyList<ChangedPathSnapshot>? changedPathSnapshots = null) : IReviewContextTools, IProCursorAvailabilityAware
{
    private readonly IReadOnlyList<ChangedPathSnapshot> _changedPathSnapshots = changedPathSnapshots ?? [];
    private readonly Guid? _clientId = clientId;
    private readonly ConcurrentDictionary<string, string> _fileCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FileNeighborhood> _fileNeighborhoodCache = new(StringComparer.Ordinal);
    private readonly int _iterationId = iterationId;

    private readonly IReadOnlyList<Guid>? _knowledgeSourceIds = knowledgeSourceIds?.Count > 0
        ? knowledgeSourceIds.ToList().AsReadOnly()
        : null;

    private readonly ILogger _logger = logger;
    private readonly AiReviewOptions _options = options.Value;
    private readonly IProCursorGateway _proCursorGateway = proCursorGateway;
    private readonly ScmProvider _provider = review.Repository.Host.Provider;

    private readonly string _providerScopePath = string.IsNullOrWhiteSpace(providerScopePath)
        ? review.Repository.Host.HostBaseUrl
        : providerScopePath.Trim();

    private readonly int _pullRequestNumber = review.Number;
    private readonly RepositoryRef _repository = review.Repository;
    private readonly ConcurrentDictionary<string, RepositoryOverview> _repositoryOverviewCache = new(StringComparer.Ordinal);
    private readonly string _sourceBranch = sourceBranch;
    private readonly string? _targetBranch = targetBranch;

    public bool SupportsProCursorTools => this._proCursorGateway is not DisabledProCursorGateway;

    public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
    {
        return ToolTimingCollectorContext.RecordAsync(
            ProtocolEventToolPhaseNames.ProviderApiCall,
            "Provider API call",
            () => this.LoadChangedFilesAsync(ct),
            files => $"operation=changed_files;count={files.Count}");
    }

    public Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct)
    {
        var normalizedBranch = this.NormalizeBranch(this._sourceBranch);
        return ToolTimingCollectorContext.RecordAsync(
            ProtocolEventToolPhaseNames.ScmFileTreeFetch,
            "SCM file tree fetch",
            () => this.LoadFileTreeAsync(normalizedBranch, ct),
            paths => $"branch={normalizedBranch};count={paths.Count}");
    }

    public async Task<string> GetFileContentAsync(
        string path,
        string branch,
        int startLine,
        int endLine,
        CancellationToken ct)
    {
        var normalizedPath = this.NormalizePath(path);
        if (BinaryFileDetector.IsBinary(normalizedPath))
        {
            return $"[Binary file — content not available: {normalizedPath}]";
        }

        var normalizedBranch = this.NormalizeBranch(this._sourceBranch);
        var cacheKey = $"{normalizedBranch}:{normalizedPath}";
        if (!this._fileCache.TryGetValue(cacheKey, out var content))
        {
            string? rawContent;
            try
            {
                rawContent = await ToolTimingCollectorContext.RecordAsync(
                    ProtocolEventToolPhaseNames.ScmFileContentFetch,
                    "SCM file content fetch",
                    () => this.FetchRawFileContentAsync(normalizedPath, normalizedBranch, ct),
                    fetched => fetched is null
                        ? $"path={normalizedPath};branch={normalizedBranch};missing=true"
                        : $"path={normalizedPath};branch={normalizedBranch};chars={fetched.Length}");
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(
                    ex,
                    "Failed to fetch file {Path} from branch {Branch}",
                    normalizedPath,
                    normalizedBranch);
                return string.Empty;
            }

            if (rawContent is null)
            {
                this._logger.LogWarning(
                    "File not found in repository (branch: {Branch}): {Path}",
                    normalizedBranch,
                    normalizedPath);
                return string.Empty;
            }

            var byteSize = Encoding.UTF8.GetByteCount(rawContent);
            if (byteSize > this._options.MaxFileSizeBytes)
            {
                return $"[File too large: {byteSize} bytes exceeds limit of {this._options.MaxFileSizeBytes} bytes]";
            }

            content = rawContent;
            this._fileCache[cacheKey] = content;
        }

        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        return ToolTimingCollectorContext.Record(
            ProtocolEventToolPhaseNames.ResultShaping,
            "Result shaping",
            () =>
            {
                var lines = content.Split('\n');
                var clampedStart = Math.Max(1, startLine);
                var clampedEnd = Math.Min(lines.Length, endLine);
                return clampedStart > clampedEnd ? string.Empty : string.Join("\n", lines[(clampedStart - 1)..clampedEnd]);
            },
            snippet => $"path={normalizedPath};chars={snippet.Length};start={startLine};end={endLine}");
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
            this._sourceBranch,
            this._targetBranch,
            this._changedPathSnapshots,
            this.LoadFileTreeAsync,
            this.FetchRawFileContentAsync,
            this.NormalizeBranch,
            this.NormalizePath,
            this._options.MaxFileSizeBytes,
            ct);
    }

    public Task<PathSearchResult> SearchPathsAsync(PathSearchRequest request, CancellationToken ct)
    {
        return RepositoryPathSearchExecutor.ExecuteAsync(
            request,
            this._sourceBranch,
            this._targetBranch,
            this._changedPathSnapshots,
            this.LoadFileTreeAsync,
            this.NormalizeBranch,
            this.NormalizePath,
            ct);
    }

    public async Task<RepositoryOverview> GetRepositoryOverviewAsync(string branchSide, CancellationToken ct)
    {
        var normalizedBranchSide = NormalizeBranchSide(branchSide);
        if (this._repositoryOverviewCache.TryGetValue(normalizedBranchSide, out var cached))
        {
            return cached;
        }

        var branch = RepositoryDiscoveryHelpers.ResolveBranch(
            normalizedBranchSide,
            this._sourceBranch,
            this._targetBranch,
            this.NormalizeBranch);
        if (branch is null)
        {
            return RepositoryOverview.CreateBlocked(normalizedBranchSide, RepositorySearchStatuses.InvalidRequest);
        }

        var paths = await ToolTimingCollectorContext.RecordAsync(
            ProtocolEventToolPhaseNames.ScmFileTreeFetch,
            "SCM file tree fetch",
            () => this.LoadFileTreeAsync(branch, ct),
            result => $"branch={branch};count={result.Count}");
        var overview = RepositoryOverviewBuilder.Build(normalizedBranchSide, branch, paths.Select(this.NormalizePath).ToList().AsReadOnly());
        this._repositoryOverviewCache[normalizedBranchSide] = overview;
        return overview;
    }

    public async Task<FileNeighborhood> GetFileNeighborhoodAsync(string filePath, string branchSide, CancellationToken ct)
    {
        var normalizedBranchSide = NormalizeBranchSide(branchSide);
        var normalizedPath = this.NormalizePath(filePath).TrimStart('/');
        var cacheKey = $"{normalizedBranchSide}:{normalizedPath}";
        if (this._fileNeighborhoodCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var branch = RepositoryDiscoveryHelpers.ResolveBranch(
            normalizedBranchSide,
            this._sourceBranch,
            this._targetBranch,
            this.NormalizeBranch);
        if (branch is null)
        {
            return FileNeighborhood.CreateBlocked(normalizedBranchSide, normalizedPath, RepositorySearchStatuses.InvalidRequest);
        }

        var paths = await ToolTimingCollectorContext.RecordAsync(
            ProtocolEventToolPhaseNames.ScmFileTreeFetch,
            "SCM file tree fetch",
            () => this.LoadFileTreeAsync(branch, ct),
            result => $"branch={branch};count={result.Count}");
        var neighborhood = FileNeighborhoodBuilder.Build(
            normalizedBranchSide,
            branch,
            normalizedPath,
            paths.Select(this.NormalizePath).ToList().AsReadOnly());
        this._fileNeighborhoodCache[cacheKey] = neighborhood;
        return neighborhood;
    }

    public Task<ProCursorKnowledgeAnswerDto> AskProCursorKnowledgeAsync(string question, CancellationToken ct)
    {
        if (!this._clientId.HasValue)
        {
            return Task.FromResult(
                new ProCursorKnowledgeAnswerDto(
                    "unavailable",
                    [],
                    "The current review context does not include a client identifier for ProCursor."));
        }

        return this.ExecuteKnowledgeQueryAsync(question, ct);
    }

    public Task<ProCursorSymbolInsightDto> GetProCursorSymbolInfoAsync(
        string symbol,
        string? queryMode,
        int? maxRelations,
        CancellationToken ct)
    {
        if (!this._clientId.HasValue)
        {
            return Task.FromResult(
                new ProCursorSymbolInsightDto(
                    "unavailable",
                    null,
                    false,
                    false,
                    null,
                    []));
        }

        if (this._provider != ScmProvider.AzureDevOps)
        {
            this._logger.LogInformation(
                "ProCursor review-target symbol insight is unavailable for provider {Provider}; returning unavailable status.",
                this._provider);

            return Task.FromResult(
                new ProCursorSymbolInsightDto(
                    "unavailable",
                    null,
                    false,
                    false,
                    null,
                    []));
        }

        return this.ExecuteSymbolQueryAsync(symbol, queryMode, maxRelations, ct);
    }

    private async Task<ProCursorKnowledgeAnswerDto> ExecuteKnowledgeQueryAsync(string question, CancellationToken ct)
    {
        try
        {
            return await ToolTimingCollectorContext.RecordAsync(
                ProtocolEventToolPhaseNames.ProviderApiCall,
                "Provider API call",
                () => this._proCursorGateway.AskKnowledgeAsync(
                    new ProCursorKnowledgeQueryRequest(
                        this._clientId!.Value,
                        question,
                        this._knowledgeSourceIds,
                        new ProCursorRepositoryContextDto(
                            this._providerScopePath,
                            this._repository.OwnerOrNamespace,
                            this._repository.ExternalRepositoryId,
                            this.NormalizeBranch(this._sourceBranch))),
                    ct),
                result => $"operation=procursor_knowledge;status={result.Status};results={result.Results.Count}");
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            this._logger.LogWarning(ex, "ProCursor knowledge query unavailable during review context execution.");
            return new ProCursorKnowledgeAnswerDto("unavailable", [], ex.Message);
        }
    }

    private async Task<ProCursorSymbolInsightDto> ExecuteSymbolQueryAsync(
        string symbol,
        string? queryMode,
        int? maxRelations,
        CancellationToken ct)
    {
        try
        {
            return await ToolTimingCollectorContext.RecordAsync(
                ProtocolEventToolPhaseNames.ProviderApiCall,
                "Provider API call",
                () => this._proCursorGateway.GetSymbolInsightAsync(
                    new ProCursorSymbolQueryRequest(
                        this._clientId!.Value,
                        symbol,
                        string.IsNullOrWhiteSpace(queryMode) ? "name" : queryMode.Trim(),
                        StateMode: "reviewTarget",
                        ReviewContext: new ProCursorReviewContextDto(
                            this._repository.ExternalRepositoryId,
                            this.NormalizeBranch(this._sourceBranch),
                            this._pullRequestNumber,
                            this._iterationId),
                        MaxRelations: maxRelations),
                    ct),
                result => $"operation=procursor_symbol;status={result.Status};has_symbol={result.Symbol is not null}");
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            this._logger.LogWarning(ex, "ProCursor symbol query unavailable during review context execution.");
            return new ProCursorSymbolInsightDto("unavailable", null, false, false, null, [], ex.Message);
        }
    }

    protected abstract Task<IReadOnlyList<ChangedFileSummary>> LoadChangedFilesAsync(CancellationToken ct);

    protected abstract Task<IReadOnlyList<string>> LoadFileTreeAsync(string normalizedBranch, CancellationToken ct);

    protected internal abstract Task<string?> FetchRawFileContentAsync(
        string normalizedPath,
        string normalizedBranch,
        CancellationToken ct);

    private Task<RepositorySearchResult> SearchAsync(RepositorySearchRequest request, CancellationToken ct)
    {
        return RepositorySearchExecutor.ExecuteAsync(
            request,
            this._sourceBranch,
            this._targetBranch,
            this._changedPathSnapshots,
            this.LoadFileTreeAsync,
            this.FetchRawFileContentAsync,
            this.NormalizeBranch,
            this.NormalizePath,
            this._options.MaxFileSizeBytes,
            ct);
    }

    protected virtual string NormalizeBranch(string branch)
    {
        return branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch;
    }

    protected virtual string NormalizePath(string path)
    {
        return path.Trim();
    }

    private static string NormalizeBranchSide(string branchSide)
    {
        return string.Equals(branchSide, RepositorySearchBranchSides.Target, StringComparison.OrdinalIgnoreCase)
            ? RepositorySearchBranchSides.Target
            : RepositorySearchBranchSides.Source;
    }
}
