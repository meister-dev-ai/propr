// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Portable non-secret configuration for one offline review execution.
/// </summary>
public enum ReviewAugmentationMode
{
    /// <summary>
    ///     Augmentation mode is disabled. No ProRV execution or integration will take place, and no related configuration properties need to be set.
    /// </summary>
    Disabled,

    /// <summary>
    ///     Augmentation mode is enabled for early steering. ProRV execution and integration will take place during the early stages of the review process.
    /// </summary>
    EarlySteering,

    /// <summary>
    ///     Augmentation mode is enabled for late augmentation. ProRV execution and integration will take place during the later stages of the review process, such as
    ///     synthesis and finalization.
    /// </summary>
    LateAugmentation,
}

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
    float? Temperature = null,
    bool EnableProRV = true,
    ReviewAugmentationMode? AugmentationMode = null,
    EvaluationProCursorContext? ProCursor = null)
{
    /// <summary>Protected values that must be resolved before execution begins.</summary>
    public IReadOnlyList<ProtectedValueReference> ProtectedValueReferencesOrEmpty => this.ProtectedValueReferences ?? [];

    /// <summary>Optional metadata labels used to identify the run configuration.</summary>
    public IReadOnlyDictionary<string, string> RunMetadataOrEmpty => this.RunMetadata ?? new Dictionary<string, string>();

    /// <summary>
    ///     Effective augmentation mode for this evaluation run. Explicit mode wins; otherwise preserve existing
    ///     boolean compatibility for older configuration files.
    /// </summary>
    public ReviewAugmentationMode EffectiveAugmentationMode => this.AugmentationMode
                                                               ?? (this.EnableProRV ? ReviewAugmentationMode.EarlySteering : ReviewAugmentationMode.Disabled);
}

/// <summary>
///     Optional ProCursor context used by offline evaluation runs.
/// </summary>
public sealed record EvaluationProCursorContext(
    Guid? ClientId = null,
    IReadOnlyList<Guid>? KnowledgeSourceIds = null)
{
    /// <summary>Selected ProCursor source identifiers to scope offline review queries.</summary>
    public IReadOnlyList<Guid> KnowledgeSourceIdsOrEmpty => this.KnowledgeSourceIds ?? [];
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
