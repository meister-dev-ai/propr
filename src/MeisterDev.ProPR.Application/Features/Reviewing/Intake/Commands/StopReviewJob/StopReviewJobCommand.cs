// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.StopReviewJob;

/// <summary>Command for manually stopping a running or queued review job.</summary>
public sealed record StopReviewJobCommand(Guid JobId);
