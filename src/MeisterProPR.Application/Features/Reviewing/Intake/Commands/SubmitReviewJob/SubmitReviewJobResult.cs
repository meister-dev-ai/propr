// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;

/// <summary>Result returned when a review job is accepted or deduplicated.</summary>
public sealed record SubmitReviewJobResult(Guid JobId, JobStatus Status, bool IsDuplicate);
