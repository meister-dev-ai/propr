// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
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
    string? providerScopePath = null) : IReviewContextTools
{
    private readonly Guid? _clientId = clientId;
    private readonly Dictionary<string, string> _fileCache = new(StringComparer.Ordinal);
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
    private readonly string _sourceBranch = sourceBranch;

    public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
    {
        return this.LoadChangedFilesAsync(ct);
    }

    public Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct)
    {
        return this.LoadFileTreeAsync(this.NormalizeBranch(this._sourceBranch), ct);
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
                rawContent = await this.FetchRawFileContentAsync(normalizedPath, normalizedBranch, ct);
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

        var lines = content.Split('\n');
        var clampedStart = Math.Max(1, startLine);
        var clampedEnd = Math.Min(lines.Length, endLine);
        return clampedStart > clampedEnd ? string.Empty : string.Join("\n", lines[(clampedStart - 1)..clampedEnd]);
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

        return this._proCursorGateway.AskKnowledgeAsync(
            new ProCursorKnowledgeQueryRequest(
                this._clientId.Value,
                question,
                this._knowledgeSourceIds,
                new ProCursorRepositoryContextDto(
                    this._providerScopePath,
                    this._repository.OwnerOrNamespace,
                    this._repository.ExternalRepositoryId,
                    this.NormalizeBranch(this._sourceBranch))),
            ct);
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

        return this._proCursorGateway.GetSymbolInsightAsync(
            new ProCursorSymbolQueryRequest(
                this._clientId.Value,
                symbol,
                string.IsNullOrWhiteSpace(queryMode) ? "name" : queryMode.Trim(),
                StateMode: "reviewTarget",
                ReviewContext: new ProCursorReviewContextDto(
                    this._repository.ExternalRepositoryId,
                    this.NormalizeBranch(this._sourceBranch),
                    this._pullRequestNumber,
                    this._iterationId),
                MaxRelations: maxRelations),
            ct);
    }

    protected abstract Task<IReadOnlyList<ChangedFileSummary>> LoadChangedFilesAsync(CancellationToken ct);

    protected abstract Task<IReadOnlyList<string>> LoadFileTreeAsync(string normalizedBranch, CancellationToken ct);

    protected internal abstract Task<string?> FetchRawFileContentAsync(
        string normalizedPath,
        string normalizedBranch,
        CancellationToken ct);

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
}
