// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
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
}
