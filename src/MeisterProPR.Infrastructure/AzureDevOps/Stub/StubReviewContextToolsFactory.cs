// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.AzureDevOps.Stub;

/// <summary>
///     No-op implementation of <see cref="IReviewContextToolsFactory" /> used when ADO_STUB_PR is enabled.
///     Returns a <see cref="NullReviewContextTools" /> instance that produces empty results for all tool calls.
/// </summary>
public sealed class StubReviewContextToolsFactory : IReviewContextToolsFactory
{
    /// <inheritdoc />
    public IReviewContextTools Create(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string sourceBranch,
        int pullRequestId,
        int iterationId,
        Guid? clientId,
        IReadOnlyList<Guid>? knowledgeSourceIds = null)
    {
        return new NullReviewContextTools();
    }
}

/// <summary>No-op <see cref="IReviewContextTools" /> that returns empty results for all calls.</summary>
internal sealed class NullReviewContextTools : IReviewContextTools
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<ChangedFileSummary>>([]);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <inheritdoc />
    public Task<string> GetFileContentAsync(string path, string branch, int startLine, int endLine, CancellationToken ct)
    {
        return Task.FromResult("");
    }

    /// <inheritdoc />
    public Task<ProCursorKnowledgeAnswerDto> AskProCursorKnowledgeAsync(string question, CancellationToken ct)
    {
        return Task.FromResult(new ProCursorKnowledgeAnswerDto(
            "unavailable",
            [],
            "ProCursor knowledge retrieval is unavailable in stub review mode."));
    }

    /// <inheritdoc />
    public Task<ProCursorSymbolInsightDto> GetProCursorSymbolInfoAsync(
        string symbol,
        string? queryMode,
        int? maxRelations,
        CancellationToken ct)
    {
        return Task.FromResult(new ProCursorSymbolInsightDto(
            "unavailable",
            null,
            false,
            false,
            null,
            [],
            null));
    }
}
