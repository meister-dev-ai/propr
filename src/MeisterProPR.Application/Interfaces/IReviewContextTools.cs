// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Provides tools that the AI reviewer can invoke during an agentic review pass
///     to retrieve information about the pull request and repository.
/// </summary>
public interface IReviewContextTools
{
    /// <summary>Returns the list of files changed in the pull request being reviewed.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct);

    /// <summary>Returns the file tree for the specified branch.</summary>
    /// <param name="branch">Branch name to enumerate files from.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct);

    /// <summary>
    ///     Returns a range of lines from a file at a specific branch.
    ///     Returns an error string if the file exceeds the configured size limit.
    /// </summary>
    /// <param name="path">Repository-relative file path.</param>
    /// <param name="branch">Branch name to read the file from.</param>
    /// <param name="startLine">One-based line number to start reading from.</param>
    /// <param name="endLine">One-based line number to stop reading at (inclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> GetFileContentAsync(string path, string branch, int startLine, int endLine, CancellationToken ct);

    /// <summary>
    ///     Searches the source branch across the full repository and returns a structured result.
    /// </summary>
    /// <param name="searchTerm">Regex text to evaluate against searchable text files.</param>
    /// <param name="fileMask">Optional glob mask to limit candidate paths before search.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RepositorySearchResult> SearchSourceRepoAsync(string searchTerm, string? fileMask, CancellationToken ct);

    /// <summary>
    ///     Searches only changed-file paths on the source branch and returns a structured result.
    /// </summary>
    /// <param name="searchTerm">Regex text to evaluate against searchable text files.</param>
    /// <param name="fileMask">Optional glob mask to limit candidate paths before search.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RepositorySearchResult> SearchSourceChangedFilesAsync(string searchTerm, string? fileMask, CancellationToken ct);

    /// <summary>
    ///     Searches the target branch across the full repository and returns a structured result.
    /// </summary>
    /// <param name="searchTerm">Regex text to evaluate against searchable text files.</param>
    /// <param name="fileMask">Optional glob mask to limit candidate paths before search.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RepositorySearchResult> SearchTargetRepoAsync(string searchTerm, string? fileMask, CancellationToken ct);

    /// <summary>
    ///     Searches only changed-file paths on the target branch and returns a structured result.
    /// </summary>
    /// <param name="searchTerm">Regex text to evaluate against searchable text files.</param>
    /// <param name="fileMask">Optional glob mask to limit candidate paths before search.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RepositorySearchResult> SearchTargetChangedFilesAsync(string searchTerm, string? fileMask, CancellationToken ct);

    /// <summary>
    ///     Searches code content in the active review repository using an explicit search mode and filters.
    /// </summary>
    /// <param name="request">Branch-aware code-search request.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CodeSearchResult> SearchCodeAsync(CodeSearchRequest request, CancellationToken ct);

    /// <summary>
    ///     Searches repository-relative path names in the active review repository.
    /// </summary>
    /// <param name="request">Branch-aware path-search request.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PathSearchResult> SearchPathsAsync(PathSearchRequest request, CancellationToken ct);

    /// <summary>
    ///     Returns a structured branch-specific repository overview for review navigation.
    /// </summary>
    /// <param name="branchSide">Logical branch side: source or target.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RepositoryOverview> GetRepositoryOverviewAsync(string branchSide, CancellationToken ct);

    /// <summary>
    ///     Returns focused ownership and nearby-context signals for one repository-relative file.
    /// </summary>
    /// <param name="filePath">Repository-relative file path.</param>
    /// <param name="branchSide">Logical branch side: source or target.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<FileNeighborhood> GetFileNeighborhoodAsync(string filePath, string branchSide, CancellationToken ct);

    /// <summary>
    ///     Asks ProCursor a reviewer-facing knowledge question using the current review repository context.
    /// </summary>
    /// <param name="question">Natural-language question to ask against configured knowledge sources.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProCursorKnowledgeAnswerDto> AskProCursorKnowledgeAsync(string question, CancellationToken ct);

    /// <summary>
    ///     Requests symbol-aware ProCursor insight using the current review repository context.
    /// </summary>
    /// <param name="symbol">Requested symbol name, qualified name, or stable symbol key.</param>
    /// <param name="queryMode">Optional symbol lookup mode.</param>
    /// <param name="maxRelations">Optional maximum number of related edges to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProCursorSymbolInsightDto> GetProCursorSymbolInfoAsync(
        string symbol,
        string? queryMode,
        int? maxRelations,
        CancellationToken ct);

    /// <summary>
    ///     Resolves confirmed cross-file reference sites for a symbol across the local review
    ///     workspace, for every supported language. Comment/string matches
    ///     are excluded by the language backend. Never throws to the review.
    /// </summary>
    /// <param name="query">The name-based symbol query.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ReferenceLookupResult> FindReferencesAsync(SymbolReferenceQuery query, CancellationToken ct);

    /// <summary>
    ///     Resolves definition site(s) for a symbol across the local review workspace, for every
    ///     supported language. Never throws to the review.
    /// </summary>
    /// <param name="query">The name-based symbol query.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DefinitionLookupResult> GetDefinitionAsync(SymbolReferenceQuery query, CancellationToken ct);

    /// <summary>
    ///     Returns the structured fields (state, acceptance criteria, and other provider fields) of a work
    ///     item / issue linked to the pull request. Returns <c>null</c> when it cannot be found or accessed.
    ///     Never throws to the review.
    /// </summary>
    /// <param name="providerKey">Provider-native identifier of the linked item (from the eager summary).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<LinkedItemDetails?> GetLinkedItemDetailsAsync(string providerKey, CancellationToken ct);

    /// <summary>
    ///     Returns the discussion/comment thread of a work item / issue linked to the pull request, oldest
    ///     first. Returns an empty list when there is none or it cannot be accessed. Never throws to the review.
    /// </summary>
    /// <param name="providerKey">Provider-native identifier of the linked item (from the eager summary).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<LinkedItemComment>> GetLinkedItemDiscussionAsync(string providerKey, CancellationToken ct);

    /// <summary>
    ///     Resolves a related link surfaced on a linked item into a full linked-item summary. Returns
    ///     <c>null</c> when the target cannot be found or accessed. Never throws to the review.
    /// </summary>
    /// <param name="relatedTargetKey">Provider-native identifier of the related item to resolve.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<LinkedItem?> ResolveLinkedItemAsync(string relatedTargetKey, CancellationToken ct);
}
