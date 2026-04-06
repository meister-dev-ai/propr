// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Orchestrates thread memory lifecycle (store on resolve, remove on reopen) and
///     memory-augmented AI reconsideration during file review.
///     All public methods must never throw to the caller.
/// </summary>
public sealed partial class ThreadMemoryService(
    IThreadMemoryEmbedder embedder,
    IThreadMemoryRepository repository,
    IProtocolRecorder protocolRecorder,
    IMemoryActivityLog activityLog,
    IOptions<AiReviewOptions> options,
    ILogger<ThreadMemoryService> logger,
    IChatClient? chatClient = null,
    IAiConnectionRepository? aiConnectionRepository = null,
    IAiChatClientFactory? aiChatClientFactory = null) : IThreadMemoryService
{
    private const double HistoricalTextFallbackThreshold = 0.72;
    private const string EmbeddingDegradedComponent = "thread_memory_embedding";
    private const string RepositoryDegradedComponent = "thread_memory_repository";
    private const string FilePathFallbackCheck = "pull_request_file_path_memory";

    private readonly AiReviewOptions _opts = options.Value;

    /// <inheritdoc />
    public async Task HandleThreadResolvedAsync(ThreadResolvedDomainEvent evt, CancellationToken ct = default)
    {
        try
        {
            var summary = await embedder.GenerateResolutionSummaryAsync(evt.FilePath, evt.ChangeExcerpt, evt.CommentHistory, evt.ClientId, ct);

            var compositeText = BuildCompositeText(evt.FilePath, evt.ChangeExcerpt, evt.CommentHistory, summary);
            var vector = await embedder.GenerateEmbeddingAsync(compositeText, evt.ClientId, ct);

            var now = DateTimeOffset.UtcNow;
            var record = new ThreadMemoryRecord
            {
                Id = Guid.NewGuid(),
                ClientId = evt.ClientId,
                ThreadId = evt.ThreadId,
                RepositoryId = evt.RepositoryId,
                PullRequestId = evt.PullRequestId,
                FilePath = evt.FilePath,
                ChangeExcerpt = TruncateExcerpt(evt.ChangeExcerpt),
                CommentHistoryDigest = evt.CommentHistory,
                ResolutionSummary = summary,
                EmbeddingVector = vector,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await repository.UpsertAsync(record, ct);

            await activityLog.AppendAsync(new MemoryActivityLogEntry
            {
                Id = Guid.NewGuid(),
                ClientId = evt.ClientId,
                ThreadId = evt.ThreadId,
                RepositoryId = evt.RepositoryId,
                PullRequestId = evt.PullRequestId,
                Action = MemoryActivityAction.Stored,
                PreviousStatus = null,
                CurrentStatus = "resolved",
                Reason = null,
                OccurredAt = now,
            }, ct);

            LogEmbeddingStored(logger, evt.ThreadId, evt.ClientId);
        }
        catch (Exception ex)
        {
            LogProcessResolvedFailed(logger, evt.ThreadId, evt.ClientId, ex);
            await activityLog.AppendAsync(new MemoryActivityLogEntry
            {
                Id = Guid.NewGuid(),
                ClientId = evt.ClientId,
                ThreadId = evt.ThreadId,
                RepositoryId = evt.RepositoryId,
                PullRequestId = evt.PullRequestId,
                Action = MemoryActivityAction.Stored,
                PreviousStatus = null,
                CurrentStatus = "resolved",
                Reason = ex.Message,
                OccurredAt = DateTimeOffset.UtcNow,
            }, ct);
        }
    }

    /// <inheritdoc />
    public async Task HandleThreadReopenedAsync(ThreadReopenedDomainEvent evt, CancellationToken ct = default)
    {
        try
        {
            var deleted = await repository.RemoveByThreadAsync(evt.ClientId, evt.RepositoryId, evt.ThreadId, ct);
            var outcome = deleted ? "deleted" : "no_op";

            await activityLog.AppendAsync(new MemoryActivityLogEntry
            {
                Id = Guid.NewGuid(),
                ClientId = evt.ClientId,
                ThreadId = evt.ThreadId,
                RepositoryId = evt.RepositoryId,
                PullRequestId = evt.PullRequestId,
                Action = MemoryActivityAction.Removed,
                PreviousStatus = "resolved",
                CurrentStatus = "active",
                Reason = outcome,
                OccurredAt = DateTimeOffset.UtcNow,
            }, ct);

            LogEmbeddingRemoved(logger, evt.ThreadId, evt.ClientId, outcome);
        }
        catch (Exception ex)
        {
            LogProcessReopenedFailed(logger, evt.ThreadId, evt.ClientId, ex);
        }
    }

    /// <inheritdoc />
    public async Task RecordNoOpAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string? previousStatus,
        string currentStatus,
        string reason,
        CancellationToken ct = default)
    {
        try
        {
            await activityLog.AppendAsync(new MemoryActivityLogEntry
            {
                Id = Guid.NewGuid(),
                ClientId = clientId,
                ThreadId = threadId,
                RepositoryId = repositoryId,
                PullRequestId = pullRequestId,
                Action = MemoryActivityAction.NoOp,
                PreviousStatus = previousStatus,
                CurrentStatus = currentStatus,
                Reason = reason,
                OccurredAt = DateTimeOffset.UtcNow,
            }, ct);
        }
        catch (Exception ex)
        {
            LogRecordNoOpFailed(logger, threadId, clientId, ex);
        }
    }

    /// <inheritdoc />
    public async Task<ThreadMemoryRecord> DismissFindingAsync(
        Guid clientId,
        string? filePath,
        string findingMessage,
        string? label,
        CancellationToken ct = default)
    {
        var compositeText = string.IsNullOrWhiteSpace(filePath)
            ? findingMessage
            : $"{filePath}\n{findingMessage}";

        var vector = await embedder.GenerateEmbeddingAsync(compositeText, clientId, ct);

        var now = DateTimeOffset.UtcNow;
        var record = new ThreadMemoryRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ThreadId = 0,
            RepositoryId = string.Empty,
            PullRequestId = 0,
            FilePath = filePath ?? string.Empty,
            ChangeExcerpt = null,
            CommentHistoryDigest = label ?? findingMessage,
            ResolutionSummary = findingMessage,
            EmbeddingVector = vector,
            MemorySource = MemorySource.AdminDismissed,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await repository.UpsertAsync(record, ct);
        return record;
    }

    /// <inheritdoc />
    public async Task<HistoricalDuplicateSuppressionMatchDto> FindDuplicateSuppressionMatchAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string? filePath,
        string findingMessage,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(findingMessage) ||
            string.IsNullOrWhiteSpace(repositoryId) ||
            pullRequestId <= 0)
        {
            return HistoricalDuplicateSuppressionMatchDto.NoMatch();
        }

        var degradedComponents = new HashSet<string>(StringComparer.Ordinal);
        var fallbackChecks = new HashSet<string>(StringComparer.Ordinal);
        var normalizedFilePath = NormalizeFilePathForLookup(filePath);

        float[]? queryVector = null;
        try
        {
            var queryText = BuildDuplicateSuppressionQueryText(normalizedFilePath, findingMessage);
            queryVector = await embedder.GenerateEmbeddingAsync(queryText, clientId, ct);
        }
        catch (Exception ex)
        {
            degradedComponents.Add(EmbeddingDegradedComponent);
            LogDuplicateSuppressionEmbeddingFailed(logger, normalizedFilePath, clientId, ex);
        }

        if (queryVector is not null)
        {
            try
            {
                var semanticMatches = await repository.FindSimilarInPullRequestAsync(
                    clientId,
                    repositoryId,
                    pullRequestId,
                    queryVector,
                    this._opts.MemoryTopN,
                    this._opts.MemoryMinSimilarity,
                    ct);

                var semanticMatch = semanticMatches
                    .OrderByDescending(match => match.SimilarityScore)
                    .FirstOrDefault();

                if (semanticMatch is not null)
                {
                    return HistoricalDuplicateSuppressionMatchDto.Match(
                        "historical_similarity_match",
                        semanticMatch.ThreadId,
                        semanticMatch.MemoryRecordId,
                        semanticMatch.SimilarityScore,
                        degradedComponents.ToList().AsReadOnly(),
                        BuildDuplicateSuppressionDegradedCause(degradedComponents),
                        fallbackChecks.ToList().AsReadOnly());
                }
            }
            catch (Exception ex)
            {
                degradedComponents.Add(RepositoryDegradedComponent);
                LogDuplicateSuppressionLookupFailed(logger, repositoryId, pullRequestId, clientId, ex);
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedFilePath))
        {
            return HistoricalDuplicateSuppressionMatchDto.NoMatch(
                degradedComponents.ToList().AsReadOnly(),
                BuildDuplicateSuppressionDegradedCause(degradedComponents),
                fallbackChecks.ToList().AsReadOnly());
        }

        try
        {
            var filePathMatches = await repository.FindByPullRequestFilePathAsync(
                clientId,
                repositoryId,
                pullRequestId,
                normalizedFilePath,
                this._opts.MemoryTopN,
                ct);

            if (degradedComponents.Count > 0)
            {
                fallbackChecks.Add(FilePathFallbackCheck);
            }

            var bestFallbackMatch = filePathMatches
                .Select(match => new
                {
                    Match = match,
                    Score = CalculateTextSimilarity(findingMessage, match.ResolutionSummary),
                })
                .Where(candidate => candidate.Score >= HistoricalTextFallbackThreshold)
                .OrderByDescending(candidate => candidate.Score)
                .FirstOrDefault();

            if (bestFallbackMatch is not null)
            {
                return HistoricalDuplicateSuppressionMatchDto.Match(
                    "historical_similarity_match",
                    bestFallbackMatch.Match.ThreadId,
                    bestFallbackMatch.Match.MemoryRecordId,
                    (float)bestFallbackMatch.Score,
                    degradedComponents.ToList().AsReadOnly(),
                    BuildDuplicateSuppressionDegradedCause(degradedComponents),
                    fallbackChecks.ToList().AsReadOnly());
            }
        }
        catch (Exception ex)
        {
            degradedComponents.Add(RepositoryDegradedComponent);
            LogDuplicateSuppressionLookupFailed(logger, repositoryId, pullRequestId, clientId, ex);
        }

        return HistoricalDuplicateSuppressionMatchDto.NoMatch(
            degradedComponents.ToList().AsReadOnly(),
            BuildDuplicateSuppressionDegradedCause(degradedComponents),
            fallbackChecks.ToList().AsReadOnly());
    }

    /// <inheritdoc />
    public async Task<ReviewResult> RetrieveAndReconsiderAsync(
        Guid clientId,
        ReviewJob job,
        string filePath,
        string? changeExcerpt,
        ReviewResult draftResult,
        Guid? protocolId,
        CancellationToken ct = default)
    {
        try
        {
            // Build query composite text from file path + change excerpt + draft findings summary.
            var queryText = BuildQueryText(filePath, changeExcerpt, draftResult.Summary);
            var queryVector = await embedder.GenerateEmbeddingAsync(queryText, clientId, ct);

            var semanticMatches = await repository.FindSimilarAsync(
                clientId,
                queryVector,
                this._opts.MemoryTopN,
                this._opts.MemoryMinSimilarity,
                ct) ?? [];

            IReadOnlyList<ThreadMemoryMatchDto> matches = semanticMatches;
            var retrievalMode = semanticMatches.Count > 0 ? "semantic_similarity" : "no_match";

            if (matches.Count == 0)
            {
                var exactFileMatches = await repository.FindByFilePathAsync(
                    clientId,
                    job.RepositoryId,
                    filePath,
                    this._opts.MemoryTopN,
                    ct) ?? [];

                if (exactFileMatches.Count > 0)
                {
                    matches = exactFileMatches;
                    retrievalMode = "exact_file_fallback";
                }
            }

            var similarityScores = semanticMatches.Select(m => m.SimilarityScore).ToList();

            if (protocolId.HasValue)
            {
                var retrievalDetails = JsonSerializer.Serialize(new
                {
                    filePath,
                    resultCount = matches.Count,
                    topN = this._opts.MemoryTopN,
                    minSimilarity = this._opts.MemoryMinSimilarity,
                    retrievalMode,
                    similarityScores,
                    matchSources = matches.Select(m => new
                    {
                        m.MemoryRecordId,
                        m.ThreadId,
                        m.FilePath,
                        m.SimilarityScore,
                        m.MatchSource,
                    }).ToList(),
                });
                await protocolRecorder.RecordMemoryEventAsync(
                    protocolId.Value, "memory_retrieval_executed", retrievalDetails, null, ct);
            }

            if (matches.Count == 0)
            {
                return draftResult;
            }

            // Reconsider draft findings with AI.
            var effectiveModelId = job.AiModel ?? this._opts.ModelId;
            var reconsideredResult = await this.ReconsiderWithAiAsync(clientId, draftResult, matches, effectiveModelId, protocolId, ct);

            if (protocolId.HasValue && reconsideredResult is not null)
            {
                // Compute a diff between draft and reconsidered comments for traceability.
                static string CommentKey(ReviewComment c)
                {
                    var msg = c.Message ?? string.Empty;
                    return $"{c.FilePath}:{c.LineNumber}:{(msg.Length > 80 ? msg[..80] : msg)}";
                }

                var reconsideredKeys = new HashSet<string>(reconsideredResult.Comments.Select(CommentKey));

                var discarded = draftResult.Comments
                    .Where(c => !reconsideredKeys.Contains(CommentKey(c)))
                    .Select(c =>
                    {
                        var msg = c.Message ?? string.Empty;
                        return new
                        {
                            filePath = c.FilePath,
                            lineNumber = c.LineNumber,
                            severity = c.Severity.ToString().ToLowerInvariant(),
                            message = msg.Length > 80 ? msg[..80] : msg,
                        };
                    })
                    .ToList();

                var draftSeverityByKey = new Dictionary<string, Domain.Enums.CommentSeverity>();
                foreach (var c in draftResult.Comments)
                {
                    draftSeverityByKey.TryAdd(CommentKey(c), c.Severity);
                }

                var downgraded = reconsideredResult.Comments
                    .Where(c => draftSeverityByKey.TryGetValue(CommentKey(c), out var origSev) && origSev != c.Severity)
                    .Select(c =>
                    {
                        draftSeverityByKey.TryGetValue(CommentKey(c), out var origSev);
                        var msg = c.Message ?? string.Empty;
                        return new
                        {
                            filePath = c.FilePath,
                            lineNumber = c.LineNumber,
                            originalSeverity = origSev.ToString().ToLowerInvariant(),
                            newSeverity = c.Severity.ToString().ToLowerInvariant(),
                            message = msg.Length > 80 ? msg[..80] : msg,
                        };
                    })
                    .ToList();

                var reconsiderationDetails = JsonSerializer.Serialize(new
                {
                    filePath,
                    contributingMemoryIds = matches.Select(m => m.MemoryRecordId).ToList(),
                    originalCommentCount = draftResult.Comments.Count,
                    finalCommentCount = reconsideredResult.Comments.Count,
                    retainedCount = reconsideredResult.Comments.Count - downgraded.Count,
                    discardedCount = discarded.Count,
                    downgradedCount = downgraded.Count,
                    discarded,
                    downgraded,
                });
                await protocolRecorder.RecordMemoryEventAsync(
                    protocolId.Value, "memory_reconsideration_completed", reconsiderationDetails, null, ct);
            }
            else if (protocolId.HasValue)
            {
                // AI returned null (empty response, parse failure, or inner exception).
                // Emit a protocol event so the trace is not left dangling after memory_retrieval_executed.
                var failureDetails = JsonSerializer.Serialize(new
                {
                    filePath,
                    contributingMemoryIds = matches.Select(m => m.MemoryRecordId).ToList(),
                    originalCommentCount = draftResult.Comments.Count,
                    reason = "ai_returned_null_or_parse_failed",
                });

                await protocolRecorder.RecordMemoryEventAsync(
                    protocolId.Value,
                    "memory_reconsideration_failed",
                    failureDetails,
                    "Reconsideration AI call returned no usable result; draft findings retained unchanged.",
                    ct);

                LogReconsiderationFallback(logger, filePath, clientId);
            }

            return reconsideredResult ?? draftResult;
        }
        catch (Exception ex)
        {
            LogRetrieveAndReconsiderFailed(logger, filePath, clientId, ex);
            if (protocolId.HasValue)
            {
                var details = JsonSerializer.Serialize(new { filePath, operationType = "retrieve_and_reconsider" });
                await protocolRecorder.RecordMemoryEventAsync(
                    protocolId.Value, "memory_operation_failed", details, ex.Message, ct);
            }

            return draftResult;
        }
    }

    private async Task<ReviewResult?> ReconsiderWithAiAsync(
        Guid clientId,
        ReviewResult draftResult,
        IReadOnlyList<ThreadMemoryMatchDto> matches,
        string modelId,
        Guid? protocolId,
        CancellationToken ct)
    {
        var effectiveChatClient = chatClient;
        var effectiveModelId = modelId;

        if (effectiveChatClient is null)
        {
            if (aiConnectionRepository is null || aiChatClientFactory is null)
            {
                return null;
            }

            var activeConnection = await aiConnectionRepository.GetActiveForClientAsync(clientId, ct);
            if (activeConnection is null)
            {
                return null;
            }

            effectiveModelId = activeConnection.ActiveModel ?? activeConnection.Models.FirstOrDefault() ?? effectiveModelId;
            effectiveChatClient = aiChatClientFactory.CreateClient(activeConnection.EndpointUrl, activeConnection.ApiKey);
        }

        try
        {
            var draftJson = JsonSerializer.Serialize(new
            {
                summary = draftResult.Summary,
                comments = draftResult.Comments.Select(c => new
                {
                    file_path = c.FilePath,
                    line_number = c.LineNumber,
                    severity = c.Severity.ToString().ToLowerInvariant(),
                    message = c.Message,
                }),
            });

            var systemMsg = BuildReconsiderationSystemPrompt();
            var userMsg = BuildReconsiderationUserMessage(draftJson, matches);

            var messages = new[]
            {
                new ChatMessage(ChatRole.System, systemMsg),
                new ChatMessage(ChatRole.User, userMsg),
            };

            var response = await effectiveChatClient.GetResponseAsync(messages, new ChatOptions { ModelId = effectiveModelId }, ct);
            var text = response.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // Record the reconsideration AI call in the protocol for full token traceability.
            if (protocolId.HasValue)
            {
                var inputTokens = response.Usage?.InputTokenCount;
                var outputTokens = response.Usage?.OutputTokenCount;
                await protocolRecorder.RecordAiCallAsync(
                    protocolId.Value,
                    0,
                    inputTokens,
                    outputTokens,
                    userMsg,
                    text,
                    ct,
                    name: "ai_call_memory_reconsideration");
                await protocolRecorder.AddTokensAsync(
                    protocolId.Value,
                    inputTokens ?? 0,
                    outputTokens ?? 0,
                    connectionCategory: Domain.Enums.AiConnectionModelCategory.MemoryReconsideration,
                    modelId: effectiveModelId,
                    ct: ct);
            }

            // Parse the AI response using the same mechanism as the main review loop.
            return ParseReconsiderationResponse(text, draftResult);
        }
        catch (Exception ex)
        {
            LogReconsiderationAiFailed(logger, ex);
            return null;
        }
    }

    private static string BuildReconsiderationSystemPrompt() =>
        """
        You are an expert code reviewer with access to historical memory of past PR review decisions.
        You are in the RECONSIDERATION phase — you will be given draft findings from an initial review pass
        alongside records of how similar issues were resolved previously in this codebase.

        Your task: evaluate each draft finding against the historical context and decide whether to:
          - RETAIN: The current finding is valid even considering past resolutions (same problem recurs unfixed, or a different instance).
          - DOWNGRADE: Lower the severity if history shows the team typically accepts this pattern.
          - DISCARD: Remove the finding if a past resolution clearly demonstrates the same concern was intentionally accepted or by design.

        CRITICAL OUTPUT RULE: Your ENTIRE response must be a single raw JSON object using exactly these keys:
          "summary" (string), "comments" (array with file_path/line_number/severity/message),
          "confidence_evaluations" (array), "investigation_complete" (bool), "loop_complete" (bool).
        Do NOT wrap in markdown fences. Return only valid JSON.
        """;

    private static string BuildReconsiderationUserMessage(
        string draftFindingsJson,
        IReadOnlyList<ThreadMemoryMatchDto> matches)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Draft Findings from Initial Review");
        sb.AppendLine(draftFindingsJson);
        sb.AppendLine();
        sb.AppendLine("## Historical Memory — Past Resolved Threads and Dismissed Patterns");

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var isDismissed = m.Source == Domain.Enums.MemorySource.AdminDismissed;

            if (isDismissed)
            {
                sb.AppendLine($"### Entry {i + 1} ⚠️ ADMIN-DISMISSED PATTERN (Memory ID: {m.MemoryRecordId})");
                sb.AppendLine($"  The administrator has explicitly dismissed this pattern. **DISCARD** any finding that closely matches it.");
            }
            else if (string.Equals(m.MatchSource, "exact_file_fallback", StringComparison.Ordinal))
            {
                sb.AppendLine($"### Entry {i + 1} (Exact file fallback, Memory ID: {m.MemoryRecordId})");
            }
            else
            {
                sb.AppendLine($"### Entry {i + 1} (Similarity: {m.SimilarityScore:F2}, Memory ID: {m.MemoryRecordId})");
            }

            if (m.FilePath is not null)
            {
                sb.AppendLine($"- **File**: {m.FilePath}");
            }

            sb.AppendLine(isDismissed
                ? $"- **Dismissed pattern**: {m.ResolutionSummary}"
                : $"- **How it was resolved**: {m.ResolutionSummary}");
            sb.AppendLine();
        }

        sb.AppendLine("Reconsider the draft findings above using the historical context. Return your reconsidered findings as a JSON object.");
        return sb.ToString();
    }

    private static ReviewResult? ParseReconsiderationResponse(string json, ReviewResult draftResult)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var summary = root.TryGetProperty("summary", out var summaryEl)
                ? summaryEl.GetString() ?? draftResult.Summary
                : draftResult.Summary;

            var comments = new List<ReviewComment>();
            if (root.TryGetProperty("comments", out var commentsEl))
            {
                foreach (var commentEl in commentsEl.EnumerateArray())
                {
                    var filePath = commentEl.TryGetProperty("file_path", out var fp) ? fp.GetString() : null;
                    int? lineNumber = commentEl.TryGetProperty("line_number", out var ln) && ln.ValueKind == JsonValueKind.Number
                        ? ln.GetInt32()
                        : null;
                    var severityStr = commentEl.TryGetProperty("severity", out var sv) ? sv.GetString() : "warning";
                    var message = commentEl.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                    var severity = Enum.TryParse<Domain.Enums.CommentSeverity>(severityStr, true, out var parsed)
                        ? parsed
                        : Domain.Enums.CommentSeverity.Warning;

                    comments.Add(new ReviewComment(filePath, lineNumber, severity, message));
                }
            }

            return new ReviewResult(summary, comments.AsReadOnly());
        }
        catch
        {
            return null;
        }
    }

    private static string BuildCompositeText(
        string? filePath,
        string? changeExcerpt,
        string commentHistory,
        string resolutionSummary)
    {
        return $"File: {filePath ?? "(PR level)"}\n" +
               $"Change: {TruncateExcerpt(changeExcerpt) ?? "(not available)"}\n" +
               $"Comments: {commentHistory}\n" +
               $"Resolution: {resolutionSummary}";
    }

    private static string BuildQueryText(string filePath, string? changeExcerpt, string draftSummary)
    {
        return $"File: {filePath}\n" +
               $"Change: {TruncateExcerpt(changeExcerpt) ?? "(not available)"}\n" +
               $"Finding: {draftSummary}";
    }

    private static string BuildDuplicateSuppressionQueryText(string? filePath, string findingMessage)
    {
        return $"File: {filePath ?? "(PR level)"}\n" +
               $"Finding: {findingMessage}";
    }

    private static string? NormalizeFilePathForLookup(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var normalized = filePath.Replace('\\', '/').Trim();
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    private static string? BuildDuplicateSuppressionDegradedCause(IReadOnlyCollection<string> degradedComponents)
    {
        if (degradedComponents.Count == 0)
        {
            return null;
        }

        if (degradedComponents.Contains(EmbeddingDegradedComponent) && degradedComponents.Contains(RepositoryDegradedComponent))
        {
            return "Historical duplicate protection ran without thread-memory embeddings or repository lookups.";
        }

        if (degradedComponents.Contains(EmbeddingDegradedComponent))
        {
            return "Historical duplicate protection ran without thread-memory embeddings.";
        }

        return "Historical duplicate protection ran without thread-memory repository lookups.";
    }

    private static double CalculateTextSimilarity(string first, string second)
    {
        var firstTokens = TokenizeForSimilarity(first);
        var secondTokens = TokenizeForSimilarity(second);

        if (firstTokens.Count == 0 || secondTokens.Count == 0)
        {
            return 0d;
        }

        var intersection = firstTokens.Intersect(secondTokens).Count();
        var union = firstTokens.Union(secondTokens).Count();
        return union == 0 ? 0d : (double)intersection / union;
    }

    private static HashSet<string> TokenizeForSimilarity(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var start = 0;

        for (var index = 0; index <= text.Length; index++)
        {
            var isWordChar = index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '\'');
            if (isWordChar)
            {
                continue;
            }

            if (index > start)
            {
                var token = text[start..index].ToLowerInvariant();
                if (token.Length >= 3)
                {
                    tokens.Add(token);
                }
            }

            start = index + 1;
        }

        return tokens;
    }

    private static string? TruncateExcerpt(string? text, int maxLength = 2000)
    {
        if (text is null)
        {
            return null;
        }

        return text.Length > maxLength ? text[..maxLength] : text;
    }
}
