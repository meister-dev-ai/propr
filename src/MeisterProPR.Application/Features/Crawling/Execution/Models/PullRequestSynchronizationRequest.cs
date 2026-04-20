// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Crawling.Execution.Models;

/// <summary>Source-neutral input for shared pull-request synchronization.</summary>
public sealed record PullRequestSynchronizationRequest
{
    /// <summary>The source that triggered synchronization.</summary>
    public required PullRequestActivationSource ActivationSource { get; init; }

    /// <summary>Human-readable label used in operator-visible action summaries.</summary>
    public required string SummaryLabel { get; init; }

    /// <summary>The client that owns the pull request.</summary>
    public required Guid ClientId { get; init; }

    /// <summary>The provider-specific scope path or URL used for synchronization lookups.</summary>
    public required string ProviderScopePath { get; init; }

    /// <summary>The provider-specific project, workspace, or namespace key used for synchronization lookups.</summary>
    public required string ProviderProjectKey { get; init; }

    /// <summary>The repository containing the pull request.</summary>
    public required string RepositoryId { get; init; }

    /// <summary>The pull request identifier.</summary>
    public required int PullRequestId { get; init; }

    /// <summary>The observed pull-request status for this synchronization pass.</summary>
    public required PrStatus PullRequestStatus { get; init; }

    /// <summary>The configured reviewer identity, when already known to the caller.</summary>
    public Guid? ReviewerId { get; init; }

    /// <summary>Normalized SCM provider family for the synchronized review context.</summary>
    public ScmProvider Provider { get; init; } = ScmProvider.AzureDevOps;

    /// <summary>Normalized provider host reference when already known to the caller.</summary>
    public ProviderHostRef? Host { get; init; }

    /// <summary>Normalized repository reference when already known to the caller.</summary>
    public RepositoryRef? Repository { get; init; }

    /// <summary>Normalized review reference when already known to the caller.</summary>
    public CodeReviewRef? CodeReview { get; init; }

    /// <summary>Normalized review revision when already known to the caller.</summary>
    public ReviewRevision? ReviewRevision { get; init; }

    /// <summary>Normalized review lifecycle state when already known to the caller.</summary>
    public CodeReviewState? ReviewState { get; init; }

    /// <summary>Normalized reviewer identity when already known to the caller.</summary>
    public ReviewerIdentity? RequestedReviewerIdentity { get; init; }

    /// <summary>The caller-supplied latest iteration, when already known.</summary>
    public int? CandidateIterationId { get; init; }

    /// <summary>When false, synchronization may perform lifecycle checks but must not queue review work.</summary>
    public bool AllowReviewSubmission { get; init; } = true;

    /// <summary>Optional PR title snapshot already fetched by the caller.</summary>
    public string? PrTitle { get; init; }

    /// <summary>Optional repository display name already fetched by the caller.</summary>
    public string? RepositoryName { get; init; }

    /// <summary>Optional source branch snapshot already fetched by the caller.</summary>
    public string? SourceBranch { get; init; }

    /// <summary>Optional target branch snapshot already fetched by the caller.</summary>
    public string? TargetBranch { get; init; }

    /// <summary>Snapshotted ProCursor source-scope mode to apply to any queued review job.</summary>
    public ProCursorSourceScopeMode ProCursorSourceScopeMode { get; init; } = ProCursorSourceScopeMode.AllClientSources;

    /// <summary>Snapshotted ProCursor source IDs to apply when source scoping is enabled.</summary>
    public IReadOnlyList<Guid> ProCursorSourceIds { get; init; } = [];

    /// <summary>Selected ProCursor source IDs that are known to be invalid and should suppress queuing.</summary>
    public IReadOnlyList<Guid> InvalidProCursorSourceIds { get; init; } = [];
}
