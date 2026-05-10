// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.Infrastructure.Features.Providers.Common;
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
    private readonly Dictionary<string, string> _fileCache = new(StringComparer.Ordinal);
    private readonly AiReviewOptions _options = options.Value;

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
            fixture.RepositorySnapshot.Files
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
            var entry = fixture.RepositorySnapshot.Files.FirstOrDefault(file => string.Equals(file.Path, normalizedPath, StringComparison.Ordinal));
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
            this._fileCache[normalizedPath] = content;
        }

        if (string.IsNullOrEmpty(content))
        {
            return Task.FromResult(string.Empty);
        }

        var lines = content.Split('\n');
        var clampedStart = Math.Max(1, startLine);
        var clampedEnd = Math.Min(lines.Length, endLine);
        return Task.FromResult(clampedStart > clampedEnd ? string.Empty : string.Join("\n", lines[(clampedStart - 1)..clampedEnd]));
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
}
