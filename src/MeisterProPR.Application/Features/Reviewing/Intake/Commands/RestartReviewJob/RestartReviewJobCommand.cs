// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Intake.Commands.RestartReviewJob;

/// <summary>Command for manually restarting a failed review job.</summary>
public sealed record RestartReviewJobCommand(Guid JobId);
