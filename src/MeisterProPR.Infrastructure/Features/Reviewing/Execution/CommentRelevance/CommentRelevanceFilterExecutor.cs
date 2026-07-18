// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;

/// <summary>
///     Executes the selected comment relevance filter and handles fallback and protocol recording logic.
/// </summary>
/// <param name="filterRegistry">The registry containing available comment relevance filters.</param>
/// <param name="protocolRecorder">The protocol recorder for logging events.</param>
internal sealed class CommentRelevanceFilterExecutor(
    CommentRelevanceFilterRegistry? filterRegistry,
    IProtocolRecorder protocolRecorder)
{
    private static readonly JsonSerializerOptions CommentRelevanceJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CommentRelevanceFilterResult?> ExecuteAsync(
        CommentRelevanceFilterRequest request,
        CancellationToken ct)
    {
        if (filterRegistry is null || !filterRegistry.HasSelection)
        {
            return null;
        }

        var effectiveRequest = string.IsNullOrWhiteSpace(request.SelectedImplementationId)
            ? new CommentRelevanceFilterRequest(
                request.JobId,
                request.FileResultId,
                filterRegistry.Selection.SelectedImplementationId,
                request.FilePath,
                request.File,
                request.PullRequest,
                request.Comments,
                request.ReviewContext,
                request.ProtocolId)
            : request;

        if (!filterRegistry.TryResolveSelected(out var selectedFilter) || selectedFilter is null)
        {
            var fallback = BuildSelectionFallbackResult(effectiveRequest);
            await this.RecordProtocolAsync(effectiveRequest.ProtocolId, fallback, ReviewProtocolEventNames.CommentRelevanceFilterSelectionFallback, ct);
            return fallback;
        }

        try
        {
            var result = await selectedFilter.FilterAsync(effectiveRequest, ct);
            var eventName = ResolveRecordedEventName(result);
            await this.RecordProtocolAsync(effectiveRequest.ProtocolId, result, eventName, ct);
            await this.RecordAiUsageAsync(effectiveRequest.ProtocolId, result, ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var fallback = BuildFailureFallbackResult(effectiveRequest, ex.Message);
            await this.RecordProtocolAsync(effectiveRequest.ProtocolId, fallback, ReviewProtocolEventNames.CommentRelevanceFilterDegraded, ct);
            return fallback;
        }
    }

    private static string ResolveRecordedEventName(CommentRelevanceFilterResult result)
    {
        if (result.DegradedComponents.Contains("comment_relevance_evaluator", StringComparer.Ordinal))
        {
            return ReviewProtocolEventNames.CommentRelevanceEvaluatorDegraded;
        }

        return result.DegradedComponents.Count > 0
            ? ReviewProtocolEventNames.CommentRelevanceFilterDegraded
            : ReviewProtocolEventNames.CommentRelevanceFilterOutput;
    }

    private static CommentRelevanceFilterResult BuildSelectionFallbackResult(CommentRelevanceFilterRequest request)
    {
        return BuildFallbackResult(
            request,
            "comment_relevance_registry",
            "pre_filter_comments_retained",
            $"Selected comment relevance filter '{request.SelectedImplementationId}' was not registered.");
    }

    private static CommentRelevanceFilterResult BuildFailureFallbackResult(
        CommentRelevanceFilterRequest request,
        string? error)
    {
        return BuildFallbackResult(
            request,
            request.SelectedImplementationId ?? "comment_relevance_filter",
            "pre_filter_comments_retained",
            string.IsNullOrWhiteSpace(error)
                ? "Comment relevance filter failed; pre-filter comments were retained unchanged."
                : $"Comment relevance filter failed; pre-filter comments were retained unchanged. Cause: {error}");
    }

    private static CommentRelevanceFilterResult BuildFallbackResult(
        CommentRelevanceFilterRequest request,
        string degradedComponent,
        string fallbackCheck,
        string degradedCause)
    {
        var decisions = request.Comments
            .Select(comment => new CommentRelevanceFilterDecision(
                CommentRelevanceFilterDecision.KeepDecision,
                comment,
                [],
                CommentRelevanceFilterDecision.FallbackModeSource))
            .ToList()
            .AsReadOnly();

        return new CommentRelevanceFilterResult(
            request.SelectedImplementationId ?? "unknown",
            "fallback",
            request.FilePath,
            request.Comments.Count,
            decisions,
            [degradedComponent],
            [fallbackCheck],
            degradedCause);
    }

    private async Task RecordProtocolAsync(
        Guid? protocolId,
        CommentRelevanceFilterResult result,
        string eventName,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        var output = result.ToRecordedOutput();
        var details = JsonSerializer.Serialize(
            new
            {
                implementationId = result.ImplementationId,
                implementationVersion = result.ImplementationVersion,
                filePath = result.FilePath,
                originalCommentCount = result.OriginalCommentCount,
                keptCount = result.KeptCount,
                discardedCount = result.DiscardedCount,
                degradedComponents = result.DegradedComponents,
                fallbackChecks = result.FallbackChecks,
                degradedCause = result.DegradedCause,
            });

        await protocolRecorder.RecordCommentRelevanceEventAsync(
            protocolId.Value,
            eventName,
            details,
            JsonSerializer.Serialize(output, CommentRelevanceJsonOptions),
            null,
            ct);
    }

    private async Task RecordAiUsageAsync(
        Guid? protocolId,
        CommentRelevanceFilterResult result,
        CancellationToken ct)
    {
        if (!protocolId.HasValue || result.AiTokenUsage is null)
        {
            return;
        }

        await protocolRecorder.RecordAiCallAsync(
            protocolId.Value,
            0,
            result.AiTokenUsage.InputTokens,
            result.AiTokenUsage.OutputTokens,
            JsonSerializer.Serialize(new { filePath = result.FilePath, implementationId = result.ImplementationId }),
            JsonSerializer.Serialize(new { implementationId = result.ImplementationId, result = "completed" }),
            null,
            ct,
            ReviewProtocolEventNames.CommentRelevanceEvaluatorAiCall,
            cachedInputTokens: result.AiTokenUsage.CachedInputTokens,
            cacheWriteTokens: result.AiTokenUsage.CacheWriteTokens,
            reasoningTokens: result.AiTokenUsage.ReasoningTokens);

        await protocolRecorder.AddTokensAsync(
            protocolId.Value,
            result.AiTokenUsage.InputTokens,
            result.AiTokenUsage.OutputTokens,
            result.AiTokenUsage.ModelCategory,
            result.AiTokenUsage.ModelId,
            ct,
            result.AiTokenUsage.CachedInputTokens,
            result.AiTokenUsage.CacheWriteTokens,
            result.AiTokenUsage.ReasoningTokens);
    }
}
