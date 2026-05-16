// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>Represents one completed Reviewing pipeline stage execution.</summary>
public sealed record ReviewStageExecution(
    PipelineStageRegistration Registration,
    ReviewStageTelemetry Telemetry);
