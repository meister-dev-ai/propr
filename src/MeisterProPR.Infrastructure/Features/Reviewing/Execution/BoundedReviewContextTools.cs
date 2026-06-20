// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

/// <summary>
///     Wraps review-context tools with Stage B investigation limits so PR-wide investigations stay
///     within their assigned tool, scope, and budget boundaries.
/// </summary>
public sealed class BoundedReviewContextTools : IReviewContextTools
{
    public const string GetChangedFilesToolName = "get_changed_files";
    public const string GetFileTreeToolName = "get_file_tree";
    public const string GetFileContentToolName = "get_file_content";
    public const string SearchSourceRepoToolName = "search_source_repo";
    public const string SearchSourceChangedFilesToolName = "search_source_changed_files";
    public const string SearchTargetRepoToolName = "search_target_repo";
    public const string SearchTargetChangedFilesToolName = "search_target_changed_files";
    public const string SearchCodeToolName = "search_code";
    public const string SearchPathsToolName = "search_paths";
    public const string GetRepositoryOverviewToolName = "get_repository_overview";
    public const string GetFileNeighborhoodToolName = "get_file_neighborhood";
    public const string AskProCursorKnowledgeToolName = "ask_procursor_knowledge";
    public const string GetProCursorSymbolInfoToolName = "get_procursor_symbol_info";

    public const string SuccessStatus = "success";
    public const string BlockedNotAllowedStatus = "blocked_not_allowed";
    public const string BlockedBudgetExhaustedStatus = "blocked_budget_exhausted";
    public const string BlockedScopeViolationStatus = "blocked_scope_violation";
    private readonly HashSet<string> _allowedTools;

    private readonly IReviewContextTools _inner;
    private readonly int _maxToolCalls;
    private readonly HashSet<string> _scopedFilePaths;
    private int _toolCallsUsed;

    public BoundedReviewContextTools(
        IReviewContextTools inner,
        IReadOnlyCollection<string> allowedTools,
        int maxToolCalls,
        IReadOnlyCollection<string>? scopedFilePaths = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(allowedTools);

        this._inner = inner;
        this._allowedTools = new HashSet<string>(allowedTools, StringComparer.Ordinal);
        this._scopedFilePaths = new HashSet<string>(scopedFilePaths ?? [], StringComparer.OrdinalIgnoreCase);
        this._maxToolCalls = Math.Max(0, maxToolCalls);
    }

    public List<PrWideToolUsage> Attempts { get; } = [];

    public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
    {
        if (!this.TryEnterCall(GetChangedFilesToolName, null, out var blockedResult))
        {
            return Task.FromResult((IReadOnlyList<ChangedFileSummary>)blockedResult!);
        }

        return this.ExecuteAsync(GetChangedFilesToolName, null, () => this._inner.GetChangedFilesAsync(ct));
    }

    public Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct)
    {
        if (!this.TryEnterCall(GetFileTreeToolName, branch, out var blockedResult))
        {
            return Task.FromResult((IReadOnlyList<string>)blockedResult!);
        }

        return this.ExecuteAsync(GetFileTreeToolName, branch, () => this._inner.GetFileTreeAsync(branch, ct));
    }

    public Task<string> GetFileContentAsync(string path, string branch, int startLine, int endLine, CancellationToken ct)
    {
        if (this._scopedFilePaths.Count > 0 && !this._scopedFilePaths.Contains(path))
        {
            this.Attempts.Add(new PrWideToolUsage(GetFileContentToolName, BlockedScopeViolationStatus, path));
            return Task.FromResult(string.Empty);
        }

        if (!this.TryEnterCall(GetFileContentToolName, path, out var blockedResult))
        {
            return Task.FromResult((string)blockedResult!);
        }

        return this.ExecuteAsync(GetFileContentToolName, path, () => this._inner.GetFileContentAsync(path, branch, startLine, endLine, ct));
    }

    public Task<RepositorySearchResult> SearchSourceRepoAsync(string searchTerm, string? fileMask, CancellationToken ct)
    {
        return this.ExecuteSearchAsync(
            SearchSourceRepoToolName,
            new RepositorySearchRequest(searchTerm, fileMask, RepositorySearchBranchSides.Source, RepositorySearchPathScopes.Repository),
            () => this._inner.SearchSourceRepoAsync(searchTerm, fileMask, ct));
    }

    public Task<RepositorySearchResult> SearchSourceChangedFilesAsync(string searchTerm, string? fileMask, CancellationToken ct)
    {
        return this.ExecuteSearchAsync(
            SearchSourceChangedFilesToolName,
            new RepositorySearchRequest(searchTerm, fileMask, RepositorySearchBranchSides.Source, RepositorySearchPathScopes.ChangedFiles),
            () => this._inner.SearchSourceChangedFilesAsync(searchTerm, fileMask, ct));
    }

    public Task<RepositorySearchResult> SearchTargetRepoAsync(string searchTerm, string? fileMask, CancellationToken ct)
    {
        return this.ExecuteSearchAsync(
            SearchTargetRepoToolName,
            new RepositorySearchRequest(searchTerm, fileMask, RepositorySearchBranchSides.Target, RepositorySearchPathScopes.Repository),
            () => this._inner.SearchTargetRepoAsync(searchTerm, fileMask, ct));
    }

    public Task<RepositorySearchResult> SearchTargetChangedFilesAsync(string searchTerm, string? fileMask, CancellationToken ct)
    {
        return this.ExecuteSearchAsync(
            SearchTargetChangedFilesToolName,
            new RepositorySearchRequest(searchTerm, fileMask, RepositorySearchBranchSides.Target, RepositorySearchPathScopes.ChangedFiles),
            () => this._inner.SearchTargetChangedFilesAsync(searchTerm, fileMask, ct));
    }

    public Task<CodeSearchResult> SearchCodeAsync(CodeSearchRequest request, CancellationToken ct)
    {
        if (!this.TryEnterCall(SearchCodeToolName, request.QueryText, out var blockedResult))
        {
            return Task.FromResult((CodeSearchResult)blockedResult!);
        }

        return this.ExecuteAsync(SearchCodeToolName, request.QueryText, () => this._inner.SearchCodeAsync(request, ct));
    }

    public Task<PathSearchResult> SearchPathsAsync(PathSearchRequest request, CancellationToken ct)
    {
        if (!this.TryEnterCall(SearchPathsToolName, request.QueryText, out var blockedResult))
        {
            return Task.FromResult((PathSearchResult)blockedResult!);
        }

        return this.ExecuteAsync(SearchPathsToolName, request.QueryText, () => this._inner.SearchPathsAsync(request, ct));
    }

    public Task<RepositoryOverview> GetRepositoryOverviewAsync(string branchSide, CancellationToken ct)
    {
        if (!this.TryEnterCall(GetRepositoryOverviewToolName, branchSide, out var blockedResult))
        {
            return Task.FromResult((RepositoryOverview)blockedResult!);
        }

        return this.ExecuteAsync(GetRepositoryOverviewToolName, branchSide, () => this._inner.GetRepositoryOverviewAsync(branchSide, ct));
    }

    public Task<FileNeighborhood> GetFileNeighborhoodAsync(string filePath, string branchSide, CancellationToken ct)
    {
        if (this._scopedFilePaths.Count > 0 && !this._scopedFilePaths.Contains(filePath))
        {
            this.Attempts.Add(new PrWideToolUsage(GetFileNeighborhoodToolName, BlockedScopeViolationStatus, filePath));
            return Task.FromResult(FileNeighborhood.CreateBlocked(branchSide, filePath, RepositorySearchStatuses.BlockedScopeViolation));
        }

        if (!this.TryEnterCall(GetFileNeighborhoodToolName, filePath, out var blockedResult))
        {
            return Task.FromResult((FileNeighborhood)blockedResult!);
        }

        return this.ExecuteAsync(GetFileNeighborhoodToolName, filePath, () => this._inner.GetFileNeighborhoodAsync(filePath, branchSide, ct));
    }

    public Task<ProCursorKnowledgeAnswerDto> AskProCursorKnowledgeAsync(string question, CancellationToken ct)
    {
        if (!this.TryEnterCall(AskProCursorKnowledgeToolName, question, out var blockedResult))
        {
            return Task.FromResult((ProCursorKnowledgeAnswerDto)blockedResult!);
        }

        return this.ExecuteAsync(AskProCursorKnowledgeToolName, question, () => this._inner.AskProCursorKnowledgeAsync(question, ct));
    }

    public Task<ProCursorSymbolInsightDto> GetProCursorSymbolInfoAsync(
        string symbol,
        string? queryMode,
        int? maxRelations,
        CancellationToken ct)
    {
        if (!this.TryEnterCall(GetProCursorSymbolInfoToolName, symbol, out var blockedResult))
        {
            return Task.FromResult((ProCursorSymbolInsightDto)blockedResult!);
        }

        return this.ExecuteAsync(
            GetProCursorSymbolInfoToolName,
            symbol,
            () => this._inner.GetProCursorSymbolInfoAsync(symbol, queryMode, maxRelations, ct));
    }

    /// <inheritdoc />
    public Task<ReferenceLookupResult> FindReferencesAsync(SymbolReferenceQuery query, CancellationToken ct)
    {
        // Delegated directly to the inner tools. These lookups are internally bounded
        // (candidate-file cap, result/char caps, per-operation time budget) and fail-soft, so they do
        // not need the PR-wide tool-call budget gate the SCM/ProCursor tools carry.
        return this._inner.FindReferencesAsync(query, ct);
    }

    /// <inheritdoc />
    public Task<DefinitionLookupResult> GetDefinitionAsync(SymbolReferenceQuery query, CancellationToken ct)
    {
        return this._inner.GetDefinitionAsync(query, ct);
    }

    private bool TryEnterCall(string toolName, string? target, out object? blockedResult)
    {
        if (!this._allowedTools.Contains(toolName))
        {
            this.Attempts.Add(new PrWideToolUsage(toolName, BlockedNotAllowedStatus, target));
            blockedResult = CreateBlockedResult(toolName, BlockedNotAllowedStatus);
            return false;
        }

        if (this._toolCallsUsed >= this._maxToolCalls)
        {
            this.Attempts.Add(new PrWideToolUsage(toolName, BlockedBudgetExhaustedStatus, target));
            blockedResult = CreateBlockedResult(toolName, BlockedBudgetExhaustedStatus);
            return false;
        }

        this._toolCallsUsed++;
        blockedResult = null;
        return true;
    }

    private async Task<T> ExecuteAsync<T>(string toolName, string? target, Func<Task<T>> operation)
    {
        try
        {
            var result = await operation();
            this.Attempts.Add(new PrWideToolUsage(toolName, SuccessStatus, target));
            return result;
        }
        catch
        {
            this.Attempts.Add(new PrWideToolUsage(toolName, "failed", target));
            throw;
        }
    }

    private Task<RepositorySearchResult> ExecuteSearchAsync(
        string toolName,
        RepositorySearchRequest request,
        Func<Task<RepositorySearchResult>> operation)
    {
        if (!this.TryEnterCall(toolName, request.SearchTerm, out var blockedResult))
        {
            return Task.FromResult((RepositorySearchResult)blockedResult!);
        }

        return this.ExecuteAsync(toolName, request.SearchTerm, operation);
    }

    private static object CreateBlockedResult(string toolName, string status)
    {
        return toolName switch
        {
            GetChangedFilesToolName => Array.Empty<ChangedFileSummary>(),
            GetFileTreeToolName => Array.Empty<string>(),
            GetFileContentToolName => string.Empty,
            SearchSourceRepoToolName => RepositorySearchResult.CreateBlocked(
                new RepositorySearchRequest(string.Empty, null, RepositorySearchBranchSides.Source, RepositorySearchPathScopes.Repository),
                status),
            SearchSourceChangedFilesToolName => RepositorySearchResult.CreateBlocked(
                new RepositorySearchRequest(string.Empty, null, RepositorySearchBranchSides.Source, RepositorySearchPathScopes.ChangedFiles),
                status),
            SearchTargetRepoToolName => RepositorySearchResult.CreateBlocked(
                new RepositorySearchRequest(string.Empty, null, RepositorySearchBranchSides.Target, RepositorySearchPathScopes.Repository),
                status),
            SearchTargetChangedFilesToolName => RepositorySearchResult.CreateBlocked(
                new RepositorySearchRequest(string.Empty, null, RepositorySearchBranchSides.Target, RepositorySearchPathScopes.ChangedFiles),
                status),
            SearchCodeToolName => CodeSearchResult.CreateBlocked(
                new CodeSearchRequest(string.Empty, CodeSearchModes.Regex, RepositorySearchBranchSides.Source, RepositorySearchPathScopes.Repository),
                status),
            SearchPathsToolName => PathSearchResult.CreateBlocked(
                new PathSearchRequest(string.Empty, PathSearchModes.Contains, RepositorySearchBranchSides.Source, RepositorySearchPathScopes.Repository),
                status),
            GetRepositoryOverviewToolName => RepositoryOverview.CreateBlocked(RepositorySearchBranchSides.Source, status),
            GetFileNeighborhoodToolName => FileNeighborhood.CreateBlocked(RepositorySearchBranchSides.Source, string.Empty, status),
            AskProCursorKnowledgeToolName => new ProCursorKnowledgeAnswerDto(status, [], $"Tool call blocked: {status}."),
            GetProCursorSymbolInfoToolName => new ProCursorSymbolInsightDto(status, null, false, false, null, []),
            _ => string.Empty,
        };
    }
}
