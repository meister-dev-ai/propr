// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Result summary for one completed prompt experiment batch.
/// </summary>
public sealed record PromptExperimentBatchResult(
    string BatchId,
    IReadOnlyList<string> ArtifactPaths);
