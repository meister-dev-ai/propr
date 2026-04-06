// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;

namespace MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;

/// <summary>Command for submitting a new review job on behalf of a client.</summary>
public sealed record SubmitReviewJobCommand(Guid ClientId, SubmitReviewJobRequestDto Request);
