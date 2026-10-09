// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Api.Features.Reviewing.Contracts;

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

    /// <summary>Local review workspace visibility metadata associated with this pass, when available.</summary>
    public ProtocolWorkspaceDto? Workspace { get; init; }

    /// <summary>Sum of cached input tokens across AI calls where the provider reported cached usage.</summary>
    public long? TotalCachedInputTokens { get; init; }

    /// <summary>Sum of cache-write tokens across AI calls in this protocol pass.</summary>
    public long? TotalCacheWriteTokens { get; init; }

    /// <summary>Sum of reasoning tokens across AI calls in this protocol pass.</summary>
    public long? TotalReasoningTokens { get; init; }

    /// <summary>Roll-up of cache observability for this protocol pass.</summary>
    public CacheObservabilityStatus CacheObservability { get; init; } = CacheObservabilityStatus.Unknown;

    /// <summary>True when this pass was inherited from a prior same-revision retry source job.</summary>
    public bool IsInherited { get; init; }

    /// <summary>Metadata describing the source job/file pass when this pass was inherited.</summary>
    public ProtocolInheritanceDto? Inheritance { get; init; }

    /// <summary>
    ///     The kind of review pass — the <c>ReviewPassKind</c> name (e.g. <c>"Baseline"</c>,
    ///     <c>"MultiPassUnion"</c>). <see langword="null" /> for legacy rows and passes with no
    ///     meaningful kind (e.g. synthesis, which the UI derives from <see cref="Label" />).
    /// </summary>
    public string? PassKind { get; init; }

    /// <summary>Human-readable reason this pass ran (e.g. an augmentation re-review). <see langword="null" /> for baseline/legacy.</summary>
    public string? Reason { get; init; }
}

public partial record ReviewJobProtocolDto
{
    /// <summary>Projects captured protocol data into the HTTP response contract.</summary>
    public static ReviewJobProtocolDto FromApplication(MeisterDev.ProPR.Application.DTOs.ReviewJobProtocolDto value) => new(
        value.Id,
        value.JobId,
        value.AttemptNumber,
        value.Label,
        value.FileResultId,
        value.StartedAt,
        value.CompletedAt,
        value.Outcome,
        value.TotalInputTokens,
        value.TotalOutputTokens,
        value.IterationCount,
        value.ToolCallCount,
        value.FinalConfidence,
        value.AiConnectionCategory,
        value.ModelId,
        value.FinalSummary,
        value.FinalComments,
        value.Events)
    {
        Provider = value.Provider,
        ProviderScopePath = value.ProviderScopePath,
        ProviderProjectKey = value.ProviderProjectKey,
        RepositoryId = value.RepositoryId,
        PullRequestId = value.PullRequestId,
        FileOutcome = value.FileOutcome,
        FollowUp = value.FollowUp,
        RepeatedJudgment = value.RepeatedJudgment,
        ProRvPrefilter = value.ProRvPrefilter,
        AgentSession = value.AgentSession,
        Workspace = value.Workspace,
        TotalCachedInputTokens = value.TotalCachedInputTokens,
        TotalCacheWriteTokens = value.TotalCacheWriteTokens,
        TotalReasoningTokens = value.TotalReasoningTokens,
        CacheObservability = value.CacheObservability,
        IsInherited = value.IsInherited,
        Inheritance = value.Inheritance,
        PassKind = value.PassKind,
        Reason = value.Reason,
    };
}
