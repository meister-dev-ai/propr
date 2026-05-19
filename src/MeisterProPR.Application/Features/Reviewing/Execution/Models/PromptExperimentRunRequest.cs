// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     One named run inside a prompt experiment batch.
/// </summary>
public sealed record PromptExperimentRunRequest(
    string RunId,
    string VariantName,
    string ArtifactPath,
    IReadOnlyList<StagePromptVariant>? StageVariants = null,
    IReadOnlyDictionary<string, string>? RunMetadata = null)
{
    /// <summary>
    ///     The list of stage variants defined for this prompt experiment run. Each stage variant defines the prompt role (e.g. system, user, assistant) and the
    ///     content for one stage in the workflow execution.
    /// </summary>
    public IReadOnlyList<StagePromptVariant> StageVariantsOrEmpty => this.StageVariants ?? [];

    /// <summary>
    ///     The run metadata for this prompt experiment run, which can include any additional information about the run that should be recorded and stored with the
    ///     evidence.
    ///     This can be used for filtering and comparison of results between different runs and variants. If no metadata is provided, an empty dictionary will be
    ///     returned.
    /// </summary>
    public IReadOnlyDictionary<string, string> RunMetadataOrEmpty => this.RunMetadata ?? new Dictionary<string, string>();
}
