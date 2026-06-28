// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

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
    bool EnableProRV = false,
    bool EnableEvidenceBackedVerification = false,
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
public sealed record EvaluationModelSelection(
    IReadOnlyList<string> ModelIds,
    string? VerificationModelId = null,
    EvaluationTieredModels? TieredModels = null)
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
///     Optional per-purpose model assignment for one evaluation run. When present, the offline harness
///     resolves a distinct model per <see cref="AiPurpose" /> — mirroring the per-tier / per-purpose model
///     bindings configured for a client in production (cheap models for low/medium review and triage, a
///     stronger model for high-effort review and synthesis, a dedicated embedding model). When absent the
///     harness keeps its single-model behavior (every purpose uses the one configured model and triage falls
///     back to the deterministic size heuristic).
/// </summary>
public sealed record EvaluationTieredModels(
    string? LowEffort = null,
    string? MediumEffort = null,
    string? HighEffort = null,
    string? Triage = null,
    string? ProRvPrefilter = null,
    string? Embedding = null,
    string? Verification = null,
    string? Default = null)
{
    /// <summary>
    ///     Resolves the model identifier for a chat purpose, walking the same cheaper-relative fallback chain
    ///     production uses (triage → low-effort → default; the per-tier review purposes → default), and finally
    ///     to <paramref name="ultimateFallback" /> (the run's primary model). Returns <see langword="null" /> for
    ///     purposes this tiered configuration does not cover so the caller can degrade exactly as it would
    ///     without any binding.
    /// </summary>
    public string? ResolveChatModel(AiPurpose purpose, string? ultimateFallback)
    {
        return purpose switch
        {
            AiPurpose.ReviewTriage => this.Triage ?? this.LowEffort ?? this.Default ?? ultimateFallback,
            AiPurpose.ReviewLowEffort => this.LowEffort ?? this.Default ?? ultimateFallback,
            AiPurpose.ReviewMediumEffort => this.MediumEffort ?? this.Default ?? ultimateFallback,
            AiPurpose.ReviewHighEffort => this.HighEffort ?? this.Default ?? ultimateFallback,
            AiPurpose.ReviewDefault => this.Default ?? ultimateFallback,
            AiPurpose.ProRVPrefilter => this.ProRvPrefilter ?? this.Default ?? ultimateFallback,
            AiPurpose.ReviewVerification => this.Verification ?? this.Triage ?? this.LowEffort ?? this.Default ?? ultimateFallback,
            _ => null,
        };
    }
}

/// <summary>
///     Output destination and detail settings for one evaluation run.
/// </summary>
public sealed record EvaluationOutputOptions(string ArtifactPath, string DetailMode);

/// <summary>
///     Non-secret AI connection settings used by the offline harness. <see cref="Provider" /> selects which
///     client SDK builds the chat client: Azure OpenAI / AI Foundry by default, or an OpenAI-compatible
///     endpoint (plain OpenAI, or a LiteLLM proxy) when set accordingly.
/// </summary>
public sealed record EvaluationAiConnection(
    string EndpointUrl,
    string? ApiKeyReferenceName = null,
    AiProviderKind Provider = AiProviderKind.AzureOpenAi);
