// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Carries protocol data for a single review job execution attempt.
/// </summary>
/// <param name="Id">Unique identifier of the protocol record.</param>
/// <param name="JobId">The review job this protocol belongs to.</param>
/// <param name="AttemptNumber">The attempt number (always 1 for now).</param>
/// <param name="Label">Human-readable label for this protocol pass (e.g. file path or "synthesis").</param>
/// <param name="FileResultId">The file result this protocol pass belongs to, or null.</param>
/// <param name="StartedAt">When the agentic loop started.</param>
/// <param name="CompletedAt">When the agentic loop finished, or null if still in progress.</param>
/// <param name="Outcome">Short outcome string (e.g. "Completed", "Failed").</param>
/// <param name="TotalInputTokens">Sum of input tokens across all AI calls.</param>
/// <param name="TotalOutputTokens">Sum of output tokens across all AI calls.</param>
/// <param name="IterationCount">Number of loop iterations completed.</param>
/// <param name="ToolCallCount">Total tool calls made during the loop.</param>
/// <param name="FinalConfidence">Final aggregated confidence score (0–100), or null if unavailable.</param>
/// <param name="AiConnectionCategory">The effort tier used for this protocol pass. Null for legacy records.</param>
/// <param name="ModelId">The AI model deployment name used for this protocol pass. Null for legacy records.</param>
/// <param name="Events">Ordered list of events captured during the loop.</param>
public sealed record ReviewJobProtocolDto(
    Guid Id,
    Guid JobId,
    int AttemptNumber,
    string? Label,
    Guid? FileResultId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? Outcome,
    long? TotalInputTokens,
    long? TotalOutputTokens,
    int? IterationCount,
    int? ToolCallCount,
    int? FinalConfidence,
    AiConnectionModelCategory? AiConnectionCategory,
    string? ModelId,
    IReadOnlyList<ProtocolEventDto> Events);

/// <summary>
///     Carries data for a single event in a review protocol.
/// </summary>
/// <param name="Id">Unique identifier of the event.</param>
/// <param name="Kind">The kind of event (AiCall, ToolCall, MemoryOperation, or Operational).</param>
/// <param name="Name">Human-readable name of the event (e.g. "ai_call_iter_1", "get_changed_files").</param>
/// <param name="OccurredAt">UTC timestamp when the event occurred.</param>
/// <param name="InputTokens">Input tokens for this event, or null.</param>
/// <param name="OutputTokens">Output tokens for this event, or null.</param>
/// <param name="InputTextSample">First 4000 characters of input text, or null.</param>
/// <param name="SystemPrompt">First 4000 characters of the system prompt used for this AI call, or null.</param>
/// <param name="OutputSummary">First 1000 characters of output, or null.</param>
/// <param name="Error">Error message if this event failed, or null.</param>
public sealed record ProtocolEventDto(
    Guid Id,
    ProtocolEventKind Kind,
    string Name,
    DateTimeOffset OccurredAt,
    long? InputTokens,
    long? OutputTokens,
    string? InputTextSample,
    string? SystemPrompt,
    string? OutputSummary,
    string? Error);
