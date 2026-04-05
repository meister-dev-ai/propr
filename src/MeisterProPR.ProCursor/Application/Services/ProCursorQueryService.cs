// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Reads persisted ProCursor snapshots and returns sourced knowledge matches for reviewer questions.
/// </summary>
public sealed partial class ProCursorQueryService(
    IProCursorKnowledgeSourceRepository knowledgeSourceRepository,
    IProCursorIndexSnapshotRepository snapshotRepository,
    IProCursorEmbeddingService embeddingService,
    IProCursorSymbolGraphRepository symbolGraphRepository,
    IOptions<ProCursorOptions> options,
    ILogger<ProCursorQueryService> logger,
    ProCursorMiniIndexBuilder? miniIndexBuilder = null)
{
    private readonly ProCursorOptions _options = options.Value;

    /// <summary>
    ///     Executes a knowledge query against the latest ready snapshots for the eligible client sources.
    /// </summary>
    public async Task<ProCursorKnowledgeAnswerDto> AskKnowledgeAsync(
        ProCursorKnowledgeQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Question);

        var eligibleSources = await this.GetEligibleSourcesAsync(request, ct);
        if (eligibleSources.Count == 0)
        {
            LogKnowledgeUnavailableNoEligibleSources(logger, request.ClientId);
            return new ProCursorKnowledgeAnswerDto(
                "unavailable",
                [],
                "No eligible ProCursor sources are configured for this client.");
        }

        var questionTokens = Tokenize(request.Question);
        var queryEmbedding = await this.TryGenerateQueryEmbeddingAsync(
            request.ClientId,
            request.Question,
            ResolveAttributedQuerySource(eligibleSources, request),
            ct);
        var maxResults = Math.Clamp(
            request.MaxResults ?? this._options.MaxQueryResults,
            1,
            Math.Max(1, this._options.MaxQueryResults));

        var matches = new List<RankedKnowledgeMatch>();
        var anyReadySnapshot = false;

        foreach (var source in eligibleSources)
        {
            var snapshot = await snapshotRepository.GetLatestReadyAsync(source.Id, null, ct);
            if (snapshot is null)
            {
                continue;
            }

            anyReadySnapshot = true;
            var trackedBranch = source.TrackedBranches.FirstOrDefault(branch => branch.Id == snapshot.TrackedBranchId);
            var freshnessStatus = ProCursorFreshnessEvaluator.GetSnapshotFreshnessStatus(trackedBranch, snapshot);
            var chunks = await snapshotRepository.ListKnowledgeChunksAsync(snapshot.Id, ct);

            foreach (var chunk in chunks)
            {
                var score = ScoreChunk(questionTokens, queryEmbedding, chunk, source, trackedBranch, request.RepositoryContext);
                if (score.Score <= 0)
                {
                    continue;
                }

                AddRankedKnowledgeMatch(
                    matches,
                    new RankedKnowledgeMatch(
                        score.Score,
                        new ProCursorKnowledgeAnswerMatchDto(
                            source.Id,
                            source.SourceKind,
                            snapshot.Id,
                            trackedBranch?.BranchName ?? source.DefaultBranch,
                            snapshot.CommitSha,
                            chunk.SourcePath,
                            chunk.Title,
                            BuildExcerpt(chunk.ContentText, questionTokens),
                            score.MatchKind,
                            Math.Round(score.Score, 4),
                            freshnessStatus)),
                    maxResults);
            }
        }

        if (!anyReadySnapshot)
        {
            LogKnowledgeUnavailableNoReadySnapshots(logger, request.ClientId);
            return new ProCursorKnowledgeAnswerDto(
                "unavailable",
                [],
                "No ready ProCursor snapshots are available for the eligible sources.");
        }

        if (matches.Count == 0)
        {
            return new ProCursorKnowledgeAnswerDto(
                "noResult",
                [],
                "No indexed knowledge matched the requested question.");
        }

        var orderedResults = matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Match.SourceKind)
            .ThenBy(match => match.Match.ContentPath, StringComparer.OrdinalIgnoreCase)
            .Select(match => match.Match)
            .Take(maxResults)
            .ToList()
            .AsReadOnly();

        var status = orderedResults.Any(result => !string.Equals(result.FreshnessStatus, "fresh", StringComparison.OrdinalIgnoreCase))
            ? "stale"
            : "complete";

        return new ProCursorKnowledgeAnswerDto(status, orderedResults);
    }

    /// <summary>
    ///     Executes a symbol query against the latest ready snapshots for the eligible client sources.
    /// </summary>
    public async Task<ProCursorSymbolInsightDto> GetSymbolInsightAsync(
        ProCursorSymbolQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Symbol);

        if (!string.Equals(request.StateMode, "indexedSnapshot", StringComparison.OrdinalIgnoreCase))
        {
            return await this.GetReviewTargetSymbolInsightAsync(request, ct);
        }

        var eligibleSources = await this.GetEligibleSymbolSourcesAsync(request.ClientId, request.SourceId, ct);
        if (eligibleSources.Count == 0)
        {
            return new ProCursorSymbolInsightDto("unavailable", null, false, false, null, [], null);
        }

        var maxRelations = Math.Clamp(
            request.MaxRelations ?? this._options.MaxQueryResults,
            1,
            Math.Max(1, this._options.MaxQueryResults * 10));
        var anyReadySnapshot = false;
        var anySupportedSnapshot = false;

        foreach (var source in eligibleSources)
        {
            var snapshot = await snapshotRepository.GetLatestReadyAsync(source.Id, null, ct);
            if (snapshot is null)
            {
                continue;
            }

            anyReadySnapshot = true;
            if (!snapshot.SupportsSymbolQueries)
            {
                continue;
            }

            anySupportedSnapshot = true;
            var symbol = await this.ResolveSymbolAsync(snapshot.Id, request, ct);
            if (symbol is null)
            {
                continue;
            }

            var trackedBranch = source.TrackedBranches.FirstOrDefault(branch => branch.Id == snapshot.TrackedBranchId);
            var freshnessStatus = ProCursorFreshnessEvaluator.GetSnapshotFreshnessStatus(trackedBranch, snapshot);
            var relations = (await symbolGraphRepository.ListEdgesAsync(snapshot.Id, symbol.SymbolKey, maxRelations, ct))
                .Select(edge => MapRelation(edge, symbol.SymbolKey))
                .ToList()
                .AsReadOnly();

            return new ProCursorSymbolInsightDto(
                string.Equals(freshnessStatus, "fresh", StringComparison.OrdinalIgnoreCase) ? "complete" : "stale",
                snapshot.Id,
                false,
                true,
                new ProCursorSymbolMatchDto(
                    symbol.SymbolKey,
                    symbol.DisplayName,
                    symbol.SymbolKind,
                    symbol.Language,
                    symbol.Signature,
                    new ProCursorSourceLocationDto(symbol.FilePath, symbol.LineStart, symbol.LineEnd)),
                relations,
                freshnessStatus);
        }

        if (!anyReadySnapshot)
        {
            return new ProCursorSymbolInsightDto("unavailable", null, false, false, null, [], null);
        }

        if (!anySupportedSnapshot)
        {
            return new ProCursorSymbolInsightDto("unsupportedLanguage", null, false, false, null, [], null);
        }

        return new ProCursorSymbolInsightDto("notFound", null, false, true, null, [], null);
    }

    private async Task<ProCursorSymbolInsightDto> GetReviewTargetSymbolInsightAsync(
        ProCursorSymbolQueryRequest request,
        CancellationToken ct)
    {
        if (!string.Equals(request.StateMode, "reviewTarget", StringComparison.OrdinalIgnoreCase) ||
            miniIndexBuilder is null)
        {
            return new ProCursorSymbolInsightDto("unavailable", null, false, false, null, [], null);
        }

        var overlay = await miniIndexBuilder.BuildAsync(request, ct);
        if (overlay is null)
        {
            return new ProCursorSymbolInsightDto("unavailable", null, true, false, null, [], null);
        }

        if (!overlay.SupportsSymbolQueries)
        {
            return new ProCursorSymbolInsightDto(
                "unsupportedLanguage",
                overlay.BaseSnapshotId,
                true,
                false,
                null,
                [],
                overlay.FreshnessStatus);
        }

        var symbol = ResolveOverlaySymbol(overlay, request);
        if (symbol is null)
        {
            return new ProCursorSymbolInsightDto(
                "notFound",
                overlay.BaseSnapshotId,
                true,
                true,
                null,
                [],
                overlay.FreshnessStatus);
        }

        var maxRelations = Math.Clamp(
            request.MaxRelations ?? this._options.MaxQueryResults,
            1,
            Math.Max(1, this._options.MaxQueryResults * 10));
        var relations = overlay.Edges
            .Where(edge => string.Equals(edge.FromSymbolKey, symbol.SymbolKey, StringComparison.Ordinal) ||
                           string.Equals(edge.ToSymbolKey, symbol.SymbolKey, StringComparison.Ordinal))
            .Take(maxRelations)
            .Select(edge => MapRelation(edge, symbol.SymbolKey))
            .ToList()
            .AsReadOnly();

        return new ProCursorSymbolInsightDto(
            string.Equals(overlay.FreshnessStatus, "fresh", StringComparison.OrdinalIgnoreCase) ? "complete" : "stale",
            overlay.BaseSnapshotId,
            true,
            true,
            new ProCursorSymbolMatchDto(
                symbol.SymbolKey,
                symbol.DisplayName,
                symbol.SymbolKind,
                symbol.Language,
                symbol.Signature,
                new ProCursorSourceLocationDto(symbol.FilePath, symbol.LineStart, symbol.LineEnd)),
            relations,
            overlay.FreshnessStatus);
    }

    private async Task<IReadOnlyList<ProCursorKnowledgeSource>> GetEligibleSourcesAsync(
        ProCursorKnowledgeQueryRequest request,
        CancellationToken ct)
    {
        var allSources = await knowledgeSourceRepository.ListByClientAsync(request.ClientId, ct);
        var requestedSourceIds = request.KnowledgeSourceIds?.Count > 0
            ? request.KnowledgeSourceIds.ToHashSet()
            : null;

        return allSources
            .Where(source => source.IsEnabled)
            .Where(source => requestedSourceIds is null || requestedSourceIds.Contains(source.Id))
            .OrderByDescending(source => RepositoryContextMatches(source, request.RepositoryContext))
            .ThenBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, this._options.MaxSourcesPerQuery))
            .ToList()
            .AsReadOnly();
    }

    private async Task<IReadOnlyList<ProCursorKnowledgeSource>> GetEligibleSymbolSourcesAsync(
        Guid clientId,
        Guid? sourceId,
        CancellationToken ct)
    {
        var allSources = await knowledgeSourceRepository.ListByClientAsync(clientId, ct);
        return allSources
            .Where(source => source.IsEnabled)
            .Where(source => !sourceId.HasValue || source.Id == sourceId.Value)
            .OrderBy(source => source.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, this._options.MaxSourcesPerQuery))
            .ToList()
            .AsReadOnly();
    }

    private static void AddRankedKnowledgeMatch(
        List<RankedKnowledgeMatch> matches,
        RankedKnowledgeMatch candidate,
        int maxResults)
    {
        matches.Add(candidate);
        matches.Sort(RankedKnowledgeMatchComparer.Instance);

        if (matches.Count > maxResults)
        {
            matches.RemoveRange(maxResults, matches.Count - maxResults);
        }
    }

    private async Task<ProCursorSymbolRecord?> ResolveSymbolAsync(
        Guid snapshotId,
        ProCursorSymbolQueryRequest request,
        CancellationToken ct)
    {
        if (string.Equals(request.QueryMode, "symbolKey", StringComparison.OrdinalIgnoreCase))
        {
            return await symbolGraphRepository.GetBySymbolKeyAsync(snapshotId, request.Symbol.Trim(), ct);
        }

        var matches = await symbolGraphRepository.SearchAsync(snapshotId, request.Symbol.Trim(), this._options.MaxQueryResults, ct);
        if (matches.Count == 0)
        {
            return null;
        }

        return string.Equals(request.QueryMode, "qualifiedName", StringComparison.OrdinalIgnoreCase)
            ? matches.OrderBy(record => GetQualifiedNameRank(record, request.Symbol.Trim())).First()
            : matches[0];
    }

    private static ProCursorSymbolRecord? ResolveOverlaySymbol(
        ProCursorMiniIndexOverlay overlay,
        ProCursorSymbolQueryRequest request)
    {
        var queryText = request.Symbol.Trim();

        if (string.Equals(request.QueryMode, "symbolKey", StringComparison.OrdinalIgnoreCase))
        {
            return overlay.Symbols.FirstOrDefault(symbol =>
                string.Equals(symbol.SymbolKey, queryText, StringComparison.OrdinalIgnoreCase));
        }

        var matches = overlay.Symbols
            .Where(symbol =>
                string.Equals(symbol.DisplayName, queryText, StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbol.Signature, queryText, StringComparison.OrdinalIgnoreCase)
                || symbol.DisplayName.Contains(queryText, StringComparison.OrdinalIgnoreCase)
                || symbol.Signature.Contains(queryText, StringComparison.OrdinalIgnoreCase)
                || symbol.SearchText.Contains(queryText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(symbol => GetQualifiedNameRank(symbol, queryText))
            .ThenBy(symbol => symbol.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            return null;
        }

        return string.Equals(request.QueryMode, "qualifiedName", StringComparison.OrdinalIgnoreCase)
            ? matches[0]
            : matches.OrderBy(symbol => GetNameRank(symbol, queryText)).First();
    }

    private async Task<float[]?> TryGenerateQueryEmbeddingAsync(
        Guid clientId,
        string question,
        ProCursorKnowledgeSource? attributedSource,
        CancellationToken ct)
    {
        try
        {
            var usageContext = attributedSource is null
                ? null
                : new ProCursorEmbeddingUsageContext(
                    attributedSource.Id,
                    attributedSource.DisplayName,
                    $"pcquery:{clientId:N}:{attributedSource.Id:N}:{ComputeStableHash(question)}",
                    ProCursorTokenUsageCallType.Embedding,
                    null,
                    [new ProCursorTokenUsageInputContext()]);
            var embeddings = await embeddingService.GenerateEmbeddingsAsync(clientId, [question], usageContext, ct);
            return embeddings.Count > 0 ? embeddings[0] : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            LogLexicalFallback(logger, clientId, ex);
            return null;
        }
    }

    private static ProCursorKnowledgeSource? ResolveAttributedQuerySource(
        IReadOnlyList<ProCursorKnowledgeSource> eligibleSources,
        ProCursorKnowledgeQueryRequest request)
    {
        if (request.KnowledgeSourceIds?.Count == 1)
        {
            return eligibleSources.FirstOrDefault(source => source.Id == request.KnowledgeSourceIds[0]);
        }

        if (request.RepositoryContext is not null)
        {
            var matchingSources = eligibleSources
                .Where(source =>
                    source.SourceKind == ProCursorSourceKind.Repository &&
                    string.Equals(source.OrganizationUrl, request.RepositoryContext.OrganizationUrl, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(source.ProjectId, request.RepositoryContext.ProjectId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(source.RepositoryId, request.RepositoryContext.RepositoryId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matchingSources.Count == 1)
            {
                return matchingSources[0];
            }
        }

        return eligibleSources.Count == 1 ? eligibleSources[0] : null;
    }

    private static string ComputeStableHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static ChunkScore ScoreChunk(
        IReadOnlyList<string> questionTokens,
        float[]? queryEmbedding,
        ProCursorKnowledgeChunk chunk,
        ProCursorKnowledgeSource source,
        ProCursorTrackedBranch? trackedBranch,
        ProCursorRepositoryContextDto? repositoryContext)
    {
        var normalizedPath = chunk.SourcePath.ToLowerInvariant();
        var normalizedTitle = chunk.Title?.ToLowerInvariant() ?? string.Empty;
        var normalizedContent = chunk.ContentText.ToLowerInvariant();

        var tokenMatches = 0;
        var titleMatches = 0;
        var pathMatches = 0;

        foreach (var token in questionTokens)
        {
            if (normalizedContent.Contains(token, StringComparison.Ordinal))
            {
                tokenMatches++;
            }

            if (normalizedTitle.Contains(token, StringComparison.Ordinal))
            {
                titleMatches++;
            }

            if (normalizedPath.Contains(token, StringComparison.Ordinal))
            {
                pathMatches++;
            }
        }

        var lexicalScore = tokenMatches + (titleMatches * 0.8d) + (pathMatches * 0.5d);
        var semanticScore = ComputeSemanticScore(queryEmbedding, chunk.EmbeddingVector);

        if (lexicalScore <= 0 && semanticScore <= 0)
        {
            return new ChunkScore(0, "keyword");
        }

        var score = lexicalScore + (semanticScore * 1.2d);
        if (RepositoryContextMatches(source, repositoryContext))
        {
            score += 1.5d;

            if (trackedBranch is not null &&
                string.Equals(trackedBranch.BranchName, repositoryContext?.Branch, StringComparison.OrdinalIgnoreCase))
            {
                score += 0.5d;
            }
        }

        var matchKind = lexicalScore > 0 && semanticScore > 0
            ? "hybrid"
            : semanticScore > 0
                ? "semantic"
                : "keyword";

        return new ChunkScore(score / Math.Max(1, questionTokens.Count), matchKind);
    }

    private static bool RepositoryContextMatches(
        ProCursorKnowledgeSource source,
        ProCursorRepositoryContextDto? repositoryContext)
    {
        if (repositoryContext is null)
        {
            return false;
        }

        return string.Equals(source.OrganizationUrl, repositoryContext.OrganizationUrl, StringComparison.OrdinalIgnoreCase)
               && string.Equals(source.ProjectId, repositoryContext.ProjectId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(source.RepositoryId, repositoryContext.RepositoryId, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildExcerpt(string contentText, IReadOnlyList<string> questionTokens)
    {
        const int maxExcerptLength = 220;
        if (contentText.Length <= maxExcerptLength)
        {
            return contentText;
        }

        var normalizedContent = contentText.ToLowerInvariant();
        var hitIndex = questionTokens
            .Select(token => normalizedContent.IndexOf(token, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();

        var startIndex = Math.Max(0, hitIndex - 40);
        if (startIndex + maxExcerptLength > contentText.Length)
        {
            startIndex = Math.Max(0, contentText.Length - maxExcerptLength);
        }

        var excerpt = contentText.Substring(startIndex, Math.Min(maxExcerptLength, contentText.Length - startIndex)).Trim();
        return startIndex == 0 ? excerpt : $"...{excerpt}";
    }

    private static IReadOnlyList<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var buffer = new List<char>(value.Length);

        void FlushBuffer()
        {
            if (buffer.Count == 0)
            {
                return;
            }

            var token = new string(buffer.ToArray());
            buffer.Clear();

            if (token.Length >= 3)
            {
                tokens.Add(token);
            }
        }

        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer.Add(character);
            }
            else
            {
                FlushBuffer();
            }
        }

        FlushBuffer();

        if (tokens.Count == 0)
        {
            tokens.Add(value.Trim().ToLowerInvariant());
        }

        return tokens
            .Distinct(StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    private static ProCursorSymbolRelationDto MapRelation(ProCursorSymbolEdge edge, string requestedSymbolKey)
    {
        var relationKind = edge.EdgeKind;
        if (string.Equals(edge.EdgeKind, "call", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(edge.ToSymbolKey, requestedSymbolKey, StringComparison.Ordinal))
        {
            relationKind = "calledBy";
        }

        return new ProCursorSymbolRelationDto(
            relationKind,
            edge.FromSymbolKey,
            edge.ToSymbolKey,
            edge.FilePath,
            edge.LineStart,
            edge.LineEnd);
    }

    private static int GetQualifiedNameRank(ProCursorSymbolRecord record, string query)
    {
        if (string.Equals(record.Signature, query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (record.Signature.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (record.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private static int GetNameRank(ProCursorSymbolRecord record, string query)
    {
        if (string.Equals(record.DisplayName, query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (record.DisplayName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (record.Signature.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private static double ComputeSemanticScore(float[]? queryEmbedding, float[] chunkEmbedding)
    {
        if (queryEmbedding is null || queryEmbedding.Length == 0 || queryEmbedding.Length != chunkEmbedding.Length)
        {
            return 0;
        }

        double dotProduct = 0;
        double queryMagnitude = 0;
        double chunkMagnitude = 0;

        for (var index = 0; index < queryEmbedding.Length; index++)
        {
            var queryValue = queryEmbedding[index];
            var chunkValue = chunkEmbedding[index];
            dotProduct += queryValue * chunkValue;
            queryMagnitude += queryValue * queryValue;
            chunkMagnitude += chunkValue * chunkValue;
        }

        if (queryMagnitude == 0 || chunkMagnitude == 0)
        {
            return 0;
        }

        var cosineSimilarity = dotProduct / (Math.Sqrt(queryMagnitude) * Math.Sqrt(chunkMagnitude));
        return cosineSimilarity >= 0.6d ? cosineSimilarity : 0;
    }

    private sealed record RankedKnowledgeMatch(double Score, ProCursorKnowledgeAnswerMatchDto Match);

    private sealed record ChunkScore(double Score, string MatchKind);

    private sealed class RankedKnowledgeMatchComparer : IComparer<RankedKnowledgeMatch>
    {
        public static RankedKnowledgeMatchComparer Instance { get; } = new();

        public int Compare(RankedKnowledgeMatch? left, RankedKnowledgeMatch? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return 1;
            }

            if (right is null)
            {
                return -1;
            }

            var scoreComparison = right.Score.CompareTo(left.Score);
            if (scoreComparison != 0)
            {
                return scoreComparison;
            }

            var sourceKindComparison = left.Match.SourceKind.CompareTo(right.Match.SourceKind);
            if (sourceKindComparison != 0)
            {
                return sourceKindComparison;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(left.Match.ContentPath, right.Match.ContentPath);
        }
    }
}
