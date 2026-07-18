// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.ValueObjects;

/// <summary>
///     Captures the observable metrics produced by one agentic review loop execution.
/// </summary>
/// <param name="ToolCallCount">Total number of tool calls made during the loop.</param>
/// <param name="ToolCallsJson">
///     JSON-serialised array of <c>ReviewToolCall</c> records, or <see langword="null" /> when
///     none were recorded.
/// </param>
/// <param name="ConfidenceEvaluationsJson">
///     JSON-serialised array of confidence evaluations captured across all iterations,
///     or <see langword="null" /> when none were recorded.
/// </param>
/// <param name="FinalConfidence">
///     Final aggregated confidence score (0–100) at loop exit, or <see langword="null" /> when
///     unavailable.
/// </param>
/// <param name="TotalInputTokens">Sum of input tokens across all AI calls in the loop.</param>
/// <param name="TotalOutputTokens">Sum of output tokens across all AI calls in the loop.</param>
/// <param name="Iterations">Number of loop iterations completed.</param>
/// <param name="SessionMode">Overall session mode used for the loop.</param>
/// <param name="TurnsJson">JSON-serialised turn context submissions, or <see langword="null" /> when unavailable.</param>
/// <param name="FallbacksJson">JSON-serialised session fallback records, or <see langword="null" /> when unavailable.</param>
/// <param name="ProviderConversationId">Opaque provider-managed conversation identifier, when available.</param>
/// <param name="ProviderResponseId">Opaque provider-managed response-chain identifier, when available.</param>
/// <param name="ActivePromptMode">The effective prompt-submission mode at loop completion.</param>
/// <param name="TotalCachedInputTokens">Sum of cached input tokens across observable AI calls in the loop.</param>
/// <param name="TotalCacheWriteTokens">Sum of cache-write tokens across observable AI calls in the loop.</param>
/// <param name="TotalReasoningTokens">Sum of reasoning tokens across observable AI calls in the loop.</param>
/// <param name="ObservedCacheUsageDetails">Whether at least one AI call in the loop reported usage, so cache metrics were observable (independent of the hit count).</param>
public sealed record ReviewLoopMetrics(
    int ToolCallCount,
    string? ToolCallsJson,
    string? ConfidenceEvaluationsJson,
    int? FinalConfidence,
    long TotalInputTokens,
    long TotalOutputTokens,
    int Iterations,
    AgentReviewSessionMode SessionMode = AgentReviewSessionMode.StatelessReplay,
    string? TurnsJson = null,
    string? FallbacksJson = null,
    string? ProviderConversationId = null,
    string? ProviderResponseId = null,
    AgentReviewPromptMode ActivePromptMode = AgentReviewPromptMode.FullReplayFallback,
    long TotalCachedInputTokens = 0,
    long TotalCacheWriteTokens = 0,
    long TotalReasoningTokens = 0,
    bool ObservedCacheUsageDetails = false);
