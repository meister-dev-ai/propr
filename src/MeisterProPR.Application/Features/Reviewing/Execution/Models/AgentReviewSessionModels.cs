// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Session mode selected for one multi-turn review loop.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentReviewSessionMode
{
    StatelessReplay = 0,
    LocalManagedSession = 1,
    ProviderManagedSession = 2,
}

/// <summary>
///     Per-turn submission strategy used for one AI call.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewTurnContextStrategy
{
    FullContext = 0,
    DeltaContext = 1,
}

/// <summary>
///     Current lifecycle state of a review session.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentReviewSessionStatus
{
    Active = 0,
    Compacted = 1,
    Downgraded = 2,
    Completed = 3,
    Failed = 4,
}

/// <summary>
///     Prompt-submission mode used for one review turn.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentReviewPromptMode
{
    FullReplayFallback = 0,
    InitialBind = 1,
    CurrentPromptOnly = 2,
}

/// <summary>
///     Kind of continuation handle carried across turns.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SessionContinuationHandleType
{
    None = 0,
    LocalCheckpoint = 1,
    ProviderSession = 2,
    ProviderResponseChain = 3,
}

/// <summary>
///     Runtime capability flags relevant to session-aware review execution.
/// </summary>
public sealed record AgentReviewRuntimeCapabilities(
    bool SupportsProviderManagedSessions,
    bool SupportsManagedRemoteConversation,
    bool SupportsBackgroundResponses,
    bool PrefersResponsesApi,
    bool SupportsPromptCaching = false,
    bool SupportsPromptCacheRouting = false);

/// <summary>
///     Opaque continuation metadata carried across review turns.
/// </summary>
public sealed record SessionContinuationHandle(
    SessionContinuationHandleType HandleType,
    string? HandleValue,
    string? ProviderSessionId,
    string? ProviderResponseId,
    DateTimeOffset IssuedAt);

/// <summary>
///     Compact summary retained after bulky prior context is removed from live replay.
/// </summary>
public sealed record WorkingMemorySummary(
    string SummaryId,
    string SourceType,
    IReadOnlyList<string> SourceReferences,
    string SummaryText,
    int ReplacedPayloadCount,
    DateTimeOffset CreatedAt);

/// <summary>
///     Session fallback metadata recorded when the preferred mode cannot continue.
/// </summary>
public sealed record SessionFallbackRecord(
    string FallbackId,
    AgentReviewSessionMode FromMode,
    AgentReviewSessionMode ToMode,
    string Reason,
    int TurnNumber,
    string PreservedState,
    DateTimeOffset RecordedAt);

/// <summary>
///     Per-turn submission metadata projected into loop metrics and protocol diagnostics.
/// </summary>
public sealed record TurnContextSubmission(
    int TurnNumber,
    ReviewTurnContextStrategy ContextStrategy,
    AgentReviewSessionMode SessionMode,
    string? NewInputSummary,
    string? ReplayedPayloadSummary,
    string? CompactedPayloadSummary,
    long? InputTokenCount,
    long? OutputTokenCount,
    string? ContinuationHandle,
    string? ProviderSessionId,
    string? ProviderResponseId,
    DateTimeOffset RecordedAt,
    AgentReviewPromptMode PromptMode = AgentReviewPromptMode.FullReplayFallback,
    bool UsedRemoteConversation = false,
    bool UsedLocalReplay = true,
    string? RemoteConversationId = null);

/// <summary>
///     Logical session state carried across one file review loop.
/// </summary>
public sealed record AgentReviewSession(
    string LocalSessionId,
    AgentReviewSessionMode Mode,
    AgentReviewSessionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset LastUpdatedAt,
    SessionContinuationHandle? ContinuationHandle,
    IReadOnlyList<WorkingMemorySummary> WorkingMemory,
    IReadOnlyList<SessionFallbackRecord> Fallbacks,
    AgentReviewPromptMode ActivePromptMode = AgentReviewPromptMode.FullReplayFallback,
    string? RemoteConversationId = null)
{
    /// <summary>
    ///     Stable local identifier for the file review attempt that owns this session.
    /// </summary>
    public string ConversationOwnerId => this.LocalSessionId;
}
