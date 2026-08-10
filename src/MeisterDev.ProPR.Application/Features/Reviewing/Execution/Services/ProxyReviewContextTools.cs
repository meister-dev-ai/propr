// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     The review-context tools as an executor sees them: six operations reach the control plane, twelve
///     read the local working copy.
///     <para>
///         This is where the classification stops being a document and becomes the code. The split is not a
///         detail: the twelve local ones include every chatty operation a review makes (repository search,
///         cross-file references, file content), and proxying those would turn the bulk of a review into
///         network traffic. The six that remain need a credential or a service the executor must not reach.
///     </para>
///     <para>
///         The interface is unchanged, so nothing downstream knows which side answered. That is the point
///         of the port: the pipeline runs the same way in both bindings.
///     </para>
/// </summary>
public sealed class ProxyReviewContextTools(
    RunnerCallContext call,
    IRunnerToolProxy proxy,
    IReviewContextTools workspaceTools) : IReviewContextTools
{
    // ── Proxied: needs a credential or the code-knowledge service ────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
    {
        return Require(await proxy.GetChangedFilesAsync(call, ct), nameof(this.GetChangedFilesAsync)) ?? [];
    }

    /// <inheritdoc />
    public async Task<ProCursorKnowledgeAnswerDto> AskProCursorKnowledgeAsync(string question, CancellationToken ct)
    {
        var result = await proxy.AskKnowledgeAsync(call, question, ct);
        if (result.Unavailable)
        {
            // The exact shape the in-process path produces when code knowledge is not offered. A different
            // shape here would be a review that behaves differently depending on where it ran.
            return new ProCursorKnowledgeAnswerDto(
                "unavailable",
                [],
                "The code-knowledge service is not available for this installation.");
        }

        return Require(result, nameof(this.AskProCursorKnowledgeAsync))!;
    }

    /// <inheritdoc />
    public async Task<ProCursorSymbolInsightDto> GetProCursorSymbolInfoAsync(
        string symbol,
        string? queryMode,
        int? maxRelations,
        CancellationToken ct)
    {
        var result = await proxy.GetSymbolInsightAsync(call, symbol, queryMode, maxRelations, ct);
        if (result.Unavailable)
        {
            return new ProCursorSymbolInsightDto(
                "unavailable",
                null,
                false,
                false,
                null,
                [],
                "The code-knowledge service is not available for this installation.");
        }

        return Require(result, nameof(this.GetProCursorSymbolInfoAsync))!;
    }

    /// <inheritdoc />
    public async Task<LinkedItemDetails?> GetLinkedItemDetailsAsync(string providerKey, CancellationToken ct)
    {
        return Require(
            await proxy.GetLinkedItemDetailsAsync(call, providerKey, ct),
            nameof(this.GetLinkedItemDetailsAsync));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LinkedItemComment>> GetLinkedItemDiscussionAsync(
        string providerKey,
        CancellationToken ct)
    {
        return Require(
                   await proxy.GetLinkedItemDiscussionAsync(call, providerKey, ct),
                   nameof(this.GetLinkedItemDiscussionAsync))
               ?? [];
    }

    /// <inheritdoc />
    public async Task<LinkedItem?> ResolveLinkedItemAsync(string relatedTargetKey, CancellationToken ct)
    {
        return Require(
            await proxy.ResolveLinkedItemAsync(call, relatedTargetKey, ct),
            nameof(this.ResolveLinkedItemAsync));
    }

    // ── Local: reads the working copy the control plane replicated to this executor ──────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct)
    {
        return workspaceTools.GetFileTreeAsync(branch, ct);
    }

    /// <inheritdoc />
    public Task<string> GetFileContentAsync(string path, string branch, int startLine, int endLine, CancellationToken ct)
    {
        return workspaceTools.GetFileContentAsync(path, branch, startLine, endLine, ct);
    }

    /// <inheritdoc />
    public Task<RepositorySearchResult> SearchSourceRepoAsync(string searchTerm, string? fileMask, CancellationToken ct)
    {
        return workspaceTools.SearchSourceRepoAsync(searchTerm, fileMask, ct);
    }

    /// <inheritdoc />
    public Task<RepositorySearchResult> SearchSourceChangedFilesAsync(
        string searchTerm,
        string? fileMask,
        CancellationToken ct)
    {
        return workspaceTools.SearchSourceChangedFilesAsync(searchTerm, fileMask, ct);
    }

    /// <inheritdoc />
    public Task<RepositorySearchResult> SearchTargetRepoAsync(string searchTerm, string? fileMask, CancellationToken ct)
    {
        return workspaceTools.SearchTargetRepoAsync(searchTerm, fileMask, ct);
    }

    /// <inheritdoc />
    public Task<RepositorySearchResult> SearchTargetChangedFilesAsync(
        string searchTerm,
        string? fileMask,
        CancellationToken ct)
    {
        return workspaceTools.SearchTargetChangedFilesAsync(searchTerm, fileMask, ct);
    }

    /// <inheritdoc />
    public Task<CodeSearchResult> SearchCodeAsync(CodeSearchRequest request, CancellationToken ct)
    {
        return workspaceTools.SearchCodeAsync(request, ct);
    }

    /// <inheritdoc />
    public Task<PathSearchResult> SearchPathsAsync(PathSearchRequest request, CancellationToken ct)
    {
        return workspaceTools.SearchPathsAsync(request, ct);
    }

    /// <inheritdoc />
    public Task<RepositoryOverview> GetRepositoryOverviewAsync(string branchSide, CancellationToken ct)
    {
        return workspaceTools.GetRepositoryOverviewAsync(branchSide, ct);
    }

    /// <inheritdoc />
    public Task<FileNeighborhood> GetFileNeighborhoodAsync(string filePath, string branchSide, CancellationToken ct)
    {
        return workspaceTools.GetFileNeighborhoodAsync(filePath, branchSide, ct);
    }

    /// <inheritdoc />
    public Task<ReferenceLookupResult> FindReferencesAsync(SymbolReferenceQuery query, CancellationToken ct)
    {
        return workspaceTools.FindReferencesAsync(query, ct);
    }

    /// <inheritdoc />
    public Task<DefinitionLookupResult> GetDefinitionAsync(SymbolReferenceQuery query, CancellationToken ct)
    {
        return workspaceTools.GetDefinitionAsync(query, ct);
    }

    /// <summary>
    ///     Unwraps a proxied answer, or stops the review.
    ///     <para>
    ///         A refusal is not a tool failing, which the tools are built to absorb and report as an empty
    ///         answer. It means this executor no longer owns the job. Absorbing it would have the review
    ///         carry on against a job somebody else is now running and produce findings from an empty
    ///         context, so it has to stop.
    ///     </para>
    ///     <para>
    ///         A fault is a tool failing, and it throws too — as the visible, retryable tool error an
    ///         in-process provider blip produces. Absorbed into an empty answer, a 502 during a rolling
    ///         restart told the reviewer the pull request changed no files, silently.
    ///     </para>
    /// </summary>
    private static T? Require<T>(RunnerToolResult<T> result, string operation)
    {
        if (result.Refusal != RunnerCallRefusal.None)
        {
            throw new RunnerCallRefusedException(operation, result.Refusal);
        }

        if (result.Fault is { } fault)
        {
            throw new RunnerToolFaultedException(operation, fault);
        }

        return result.Value;
    }
}

/// <summary>
///     Thrown when a proxied tool call never got an answer. The pipeline's tool invoker reports it to the
///     model as an ordinary tool error, which the model can see and retry — the same behaviour the
///     in-process binding has when the service behind a tool blips.
/// </summary>
public sealed class RunnerToolFaultedException(string operation, string reason)
    : InvalidOperationException($"The proxied call '{operation}' failed: {reason}.")
{
    /// <summary>The operation that failed.</summary>
    public string Operation { get; } = operation;

    /// <summary>What went wrong in transit.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
///     Thrown when the control plane refuses a proxied call because the executor no longer holds the job.
///     Deliberately not caught by the tools' own fault handling: an executor that lost its lease must stop,
///     not degrade.
/// </summary>
public sealed class RunnerCallRefusedException(string operation, RunnerCallRefusal refusal)
    : InvalidOperationException($"The control plane refused '{operation}': {refusal}. This executor no longer holds the job.")
{
    /// <summary>The operation that was refused.</summary>
    public string Operation { get; } = operation;

    /// <summary>Why it was refused.</summary>
    public RunnerCallRefusal Refusal { get; } = refusal;
}
