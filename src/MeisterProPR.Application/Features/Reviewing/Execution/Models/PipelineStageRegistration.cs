// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>Describes one reusable Reviewing pipeline stage bound to a pipeline family.</summary>
public sealed record PipelineStageRegistration(
    string StageId,
    string StageName,
    string PipelineFamily,
    int ExecutionOrder,
    bool ProducesTelemetry,
    bool RequiresExternalRuntime);
