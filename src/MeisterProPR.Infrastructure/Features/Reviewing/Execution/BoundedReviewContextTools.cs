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
    public const string AskProCursorKnowledgeToolName = "ask_procursor_knowledge";
    public const string GetProCursorSymbolInfoToolName = "get_procursor_symbol_info";

    public const string SuccessStatus = "success";
    public const string BlockedNotAllowedStatus = "blocked_not_allowed";
    public const string BlockedBudgetExhaustedStatus = "blocked_budget_exhausted";
    public const string BlockedScopeViolationStatus = "blocked_scope_violation";

    private readonly IReviewContextTools _inner;
    private readonly HashSet<string> _allowedTools;
    private readonly HashSet<string> _scopedFilePaths;
    private readonly int _maxToolCalls;
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

    private static object CreateBlockedResult(string toolName, string status)
    {
        return toolName switch
        {
            GetChangedFilesToolName => Array.Empty<ChangedFileSummary>(),
            GetFileTreeToolName => Array.Empty<string>(),
            GetFileContentToolName => string.Empty,
            AskProCursorKnowledgeToolName => new ProCursorKnowledgeAnswerDto(status, [], $"Tool call blocked: {status}."),
            GetProCursorSymbolInfoToolName => new ProCursorSymbolInsightDto(status, null, false, false, null, []),
            _ => string.Empty,
        };
    }
}
