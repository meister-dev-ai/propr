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
/// <param name="FinalSummary">Final review summary attributable to this protocol pass, when available.</param>
/// <param name="FinalComments">Final review comments attributable to this protocol pass, when available.</param>
/// <param name="Events">Ordered list of events captured during the loop.</param>
public sealed partial record ReviewJobProtocolDto(
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
    string? FinalSummary,
    IReadOnlyList<ProtocolReviewCommentDto>? FinalComments,
    IReadOnlyList<ProtocolEventDto> Events);

/// <summary>
///     Additional review-job context projected alongside a protocol pass.
/// </summary>
public partial record ReviewJobProtocolDto
{
    /// <summary>Normalized SCM provider family for the parent review job.</summary>
    public ScmProvider Provider { get; init; } = ScmProvider.AzureDevOps;

    /// <summary>Normalized provider scope path for the parent review job.</summary>
    public string? ProviderScopePath { get; init; }

    /// <summary>Normalized provider project, owner, or namespace key for the parent review job.</summary>
    public string? ProviderProjectKey { get; init; }

    /// <summary>Repository identifier for the parent review job.</summary>
    public string? RepositoryId { get; init; }

    /// <summary>Pull request number for the parent review job.</summary>
    public int PullRequestId { get; init; }

    /// <summary>Resolved review strategy snapshotted on the parent review job.</summary>
    public ReviewStrategy ResolvedReviewStrategy { get; init; } = ReviewStrategy.FileByFile;

    /// <summary>How the parent review job selected its resolved review strategy.</summary>
    public ReviewStrategySelectionSource StrategySelectionSource { get; init; } = ReviewStrategySelectionSource.FallbackDefault;

    /// <summary>Terminal file outcome associated with this pass, when the protocol belongs to a file result.</summary>
    public ProtocolFileOutcomeDto? FileOutcome { get; init; }

    /// <summary>Follow-up usage and dependency visibility associated with this file-linked pass, when available.</summary>
    public ProtocolFollowUpDto? FollowUp { get; init; }

    /// <summary>Repeated-judgment decision details associated with this pass, when available.</summary>
    public ProtocolRepeatedJudgmentDto? RepeatedJudgment { get; init; }

    /// <summary>ProRV prefilter execution visibility metadata associated with this pass, when available.</summary>
    public ProtocolProRvPrefilterDto? ProRvPrefilter { get; init; }

    /// <summary>Managed file-review session visibility metadata associated with this pass, when available.</summary>
    public ProtocolAgentSessionDto? AgentSession { get; init; }

    /// <summary>Sum of cached input tokens across AI calls where the provider reported cached usage.</summary>
    public long? TotalCachedInputTokens { get; init; }

    /// <summary>Roll-up of cache observability for this protocol pass.</summary>
    public CacheObservabilityStatus CacheObservability { get; init; } = CacheObservabilityStatus.Unknown;

    /// <summary>True when this pass was inherited from a prior same-revision retry source job.</summary>
    public bool IsInherited { get; init; }

    /// <summary>Metadata describing the source job/file pass when this pass was inherited.</summary>
    public ProtocolInheritanceDto? Inheritance { get; init; }
}

/// <summary>
///     Inherited pass metadata projected into the current job protocol view.
/// </summary>
public sealed record ProtocolInheritanceDto(
    Guid SourceJobId,
    Guid? SourceFileResultId,
    Guid SourceProtocolId,
    DateTimeOffset? SourceCompletedAt);

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
/// <param name="EventCategory">Normalized protocol-step category captured for this event, or null for legacy rows.</param>
/// <param name="Error">Error message if this event failed, or null.</param>
public sealed partial record ProtocolEventDto(
    Guid Id,
    ProtocolEventKind Kind,
    string Name,
    DateTimeOffset OccurredAt,
    long? InputTokens,
    long? OutputTokens,
    string? InputTextSample,
    string? SystemPrompt,
    string? OutputSummary,
    string? EventCategory,
    string? Error);

/// <summary>
///     Additive cache, evidence, and finalization diagnostics projected for a single protocol event.
/// </summary>
public partial record ProtocolEventDto
{
    /// <summary>Cached input tokens read from a provider cache for this AI call.</summary>
    public long? CachedInputTokens { get; init; }

    /// <summary>Cache outcome for this AI call.</summary>
    public CacheCallStatus CacheStatus { get; init; } = CacheCallStatus.NotApplicable;

    /// <summary>Actionable miss or unavailable reason when cache status is not a hit.</summary>
    public string? CacheMissCategory { get; init; }

    /// <summary>Stable-prefix eligibility for this AI call.</summary>
    public PrefixEligibilityStatus PrefixEligibility { get; init; } = PrefixEligibilityStatus.NotApplicable;

    /// <summary>Tool evidence visibility details when this event records a bounded/summarized/refreshed result.</summary>
    public ProtocolToolEvidenceDto? ToolEvidence { get; init; }

    /// <summary>Forced-final or schema-repair attempt kind for this AI call.</summary>
    public string? FinalizationAttemptKind { get; init; }

    /// <summary>Reason for a forced-final or schema-repair attempt.</summary>
    public string? FinalizationReason { get; init; }

    /// <summary>Outcome for a forced-final or schema-repair attempt.</summary>
    public string? FinalizationOutcome { get; init; }

    /// <summary>UTC timestamp at which a tool call started when captured.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>UTC timestamp at which a tool call completed when captured.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Total elapsed duration of a tool call in milliseconds when captured.</summary>
    public long? DurationMs { get; init; }

    /// <summary>Measured wait or backoff duration in milliseconds when available.</summary>
    public long? WaitDurationMs { get; init; }

    /// <summary>Measured active execution duration in milliseconds when available.</summary>
    public long? ActiveDurationMs { get; init; }

    /// <summary>Availability state for the timing dimensions on this event.</summary>
    public string? TimingAvailability { get; init; }

    /// <summary>Outcome of the tool invocation when this event represents a tool call.</summary>
    public string? ToolOutcome { get; init; }

    /// <summary>Ordered timing phases captured inside the tool invocation when available.</summary>
    public IReadOnlyList<ProtocolEventPhaseTimingDto>? PhaseTimings { get; init; }
}

/// <summary>
///     Visibility for one tool-result evidence bounding or refresh action.
/// </summary>
public sealed record ProtocolToolEvidenceDto(
    string SourceToolName,
    int OriginalPayloadTokens,
    int BoundedPayloadTokens,
    string Action,
    bool Refreshable);

/// <summary>
///     One ordered timed phase captured inside a tool invocation.
/// </summary>
public sealed record ProtocolEventPhaseTimingDto(
    string Name,
    string DisplayName,
    int Sequence,
    int? Occurrence,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long? DurationMs,
    string Availability,
    string Outcome,
    string? Summary);

/// <summary>
///     Carries one final review comment attributable to a protocol pass.
/// </summary>
/// <param name="FilePath">Repository-relative file path, when available.</param>
/// <param name="LineNumber">One-based line number, when available.</param>
/// <param name="Severity">Normalized comment severity.</param>
/// <param name="Message">Final comment text.</param>
public sealed record ProtocolReviewCommentDto(
    string? FilePath,
    int? LineNumber,
    CommentSeverity Severity,
    string Message);

/// <summary>
///     Terminal outcome metadata for one file-linked protocol pass.
/// </summary>
public sealed record ProtocolFileOutcomeDto(
    string FilePath,
    bool IsComplete,
    bool IsFailed,
    bool IsExcluded,
    bool IsCarriedForward,
    string? ExclusionReason,
    string? ErrorMessage,
    bool IsDegraded);

/// <summary>
///     Follow-up visibility metadata for one file-linked protocol pass.
/// </summary>
public sealed record ProtocolFollowUpDto(
    bool Used,
    string? TriggerFamily,
    bool CompletedSuccessfully,
    bool DependencyRecorded);

/// <summary>
///     Repeated-judgment visibility metadata for one protocol pass.
/// </summary>
public sealed record ProtocolRepeatedJudgmentDto(
    string FindingId,
    string? EvidenceSetId,
    string AgreementState,
    string RecommendedDisposition,
    bool UsedSameEvidenceSet,
    IReadOnlyList<string> ReasonCodes);

/// <summary>
///     ProRV prefilter execution visibility metadata for one protocol pass.
/// </summary>
public sealed record ProtocolProRvPrefilterDto(
    bool Selected,
    string ExecutionState,
    string? StageId,
    string? Reason,
    string? RuntimeSource,
    string? ModelId,
    string? Language,
    string? PrefilterStatus,
    int GuidanceCount,
    bool AiCallRecorded,
    bool GuidanceApplied,
    string? AppliedPromptKind,
    IReadOnlyList<string> AppliedGuidanceIds);

/// <summary>
///     Managed file-review session visibility metadata for one protocol pass.
/// </summary>
public sealed record ProtocolAgentSessionDto(
    bool UsedManagedRemoteConversation,
    string? RemoteConversationId,
    string? BindingMethod,
    string? BindingOutcome,
    string? PromptMode,
    bool UsedLocalReplay,
    string? FallbackReason);
