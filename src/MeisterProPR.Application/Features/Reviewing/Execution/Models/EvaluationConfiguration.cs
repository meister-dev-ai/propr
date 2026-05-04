// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Portable non-secret configuration for one offline review execution.
/// </summary>
public sealed record EvaluationConfiguration(
    string ConfigurationId,
    EvaluationModelSelection ModelSelection,
    EvaluationOutputOptions Output,
    IReadOnlyList<ProtectedValueReference>? ProtectedValueReferences = null,
    IReadOnlyDictionary<string, string>? RunMetadata = null,
    EvaluationAiConnection? AiConnection = null,
    float? Temperature = null)
{
    /// <summary>Protected values that must be resolved before execution begins.</summary>
    public IReadOnlyList<ProtectedValueReference> ProtectedValueReferencesOrEmpty => this.ProtectedValueReferences ?? [];

    /// <summary>Optional metadata labels used to identify the run configuration.</summary>
    public IReadOnlyDictionary<string, string> RunMetadataOrEmpty => this.RunMetadata ?? new Dictionary<string, string>();
}

/// <summary>
///     Model-selection metadata for one evaluation run.
/// </summary>
public sealed record EvaluationModelSelection(IReadOnlyList<string> ModelIds, string? VerificationModelId = null)
{
    /// <summary>Configured model identifiers, preserving declaration order.</summary>
    public IReadOnlyList<string> ModelIdsOrEmpty => this.ModelIds ?? [];

    /// <summary>Returns the first configured model identifier for single-model callers.</summary>
    public string ModelId => this.ModelIdsOrEmpty.FirstOrDefault() ?? string.Empty;

    /// <summary>Optional model identifier to use specifically for verification/evaluation checks.</summary>
    public string? VerificationModelIdOrNull => string.IsNullOrWhiteSpace(this.VerificationModelId)
        ? null
        : this.VerificationModelId;
}

/// <summary>
///     Output destination and detail settings for one evaluation run.
/// </summary>
public sealed record EvaluationOutputOptions(string ArtifactPath, string DetailMode);

/// <summary>
///     Non-secret AI connection settings used by the offline harness.
/// </summary>
public sealed record EvaluationAiConnection(string EndpointUrl, string? ApiKeyReferenceName = null);
