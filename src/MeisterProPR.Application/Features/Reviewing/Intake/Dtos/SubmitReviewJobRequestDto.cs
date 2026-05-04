// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Intake.Dtos;

/// <summary>Request payload for submitting a pull request review job.</summary>
public sealed record SubmitReviewJobRequestDto(
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    int PullRequestId,
    int IterationId,
    float? ReviewTemperature = null)
{
    /// <summary>Normalized SCM provider family for the submitted review target.</summary>
    public ScmProvider Provider { get; init; } = ScmProvider.AzureDevOps;

    /// <summary>Normalized provider host reference when the caller can supply it.</summary>
    public ProviderHostRef? Host { get; init; }

    /// <summary>Normalized repository reference when the caller can supply it.</summary>
    public RepositoryRef? Repository { get; init; }

    /// <summary>Normalized review reference when the caller can supply it.</summary>
    public CodeReviewRef? CodeReview { get; init; }

    /// <summary>Normalized review revision when the caller can supply it.</summary>
    public ReviewRevision? ReviewRevision { get; init; }

    /// <summary>Normalized reviewer identity when the caller can supply it.</summary>
    public ReviewerIdentity? RequestedReviewerIdentity { get; init; }
}
