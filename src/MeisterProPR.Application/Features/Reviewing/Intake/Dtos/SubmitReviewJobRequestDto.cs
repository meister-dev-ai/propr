// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Intake.Dtos;

/// <summary>Request payload for submitting a pull request review job.</summary>
public sealed record SubmitReviewJobRequestDto(
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int IterationId);
