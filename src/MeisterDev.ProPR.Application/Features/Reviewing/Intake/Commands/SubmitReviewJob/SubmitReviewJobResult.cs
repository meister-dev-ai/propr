// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;

/// <summary>Result returned when a review job is accepted, deduplicated, or refused because the PR is blocked.</summary>
public sealed record SubmitReviewJobResult(
    Guid JobId,
    JobStatus Status,
    bool IsDuplicate,
    bool IsBlocked = false);
