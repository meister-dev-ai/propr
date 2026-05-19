// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     One single-fixture offline prompt experiment batch.
/// </summary>
public sealed record PromptExperimentBatch(
    string BatchId,
    string FixtureId,
    string? ScenarioId,
    string ConfigurationId,
    IReadOnlyList<PromptExperimentRunRequest> VariantRuns)
{
    /// <summary>
    ///     For convenience, allow treating missing variant runs as empty list to avoid null checks in processing code.
    /// </summary>
    public IReadOnlyList<PromptExperimentRunRequest> VariantRunsOrEmpty => this.VariantRuns ?? Array.Empty<PromptExperimentRunRequest>();
}
