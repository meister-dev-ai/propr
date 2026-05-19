// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Audit-ready prompt evidence for one executed stage.
/// </summary>
public sealed record PromptExperimentEvidence
{
    /// <summary>
    ///     Create new prompt experiment evidence record for one executed stage.
    /// </summary>
    /// <param name="stageKey">The key of the stage for which the evidence is recorded.</param>
    /// <param name="variantName">The name of the prompt experiment variant.</param>
    /// <param name="compositionMode">The composition mode of the prompt experiment.</param>
    /// <param name="usedDefaultConstruction">Indicates whether the default construction was used.</param>
    /// <param name="systemPromptText">The system prompt text, if any.</param>
    /// <param name="userPromptText">The user prompt text, if any.</param>
    /// <exception cref="ArgumentException"></exception>
    public PromptExperimentEvidence(
        string stageKey,
        string variantName,
        PromptCompositionMode compositionMode,
        bool usedDefaultConstruction,
        string? systemPromptText,
        string? userPromptText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(variantName);

        if (string.IsNullOrWhiteSpace(systemPromptText) && string.IsNullOrWhiteSpace(userPromptText))
        {
            throw new ArgumentException("At least one prompt text must be recorded for prompt experiment evidence.", nameof(systemPromptText));
        }

        this.StageKey = stageKey;
        this.VariantName = variantName;
        this.CompositionMode = compositionMode;
        this.UsedDefaultConstruction = usedDefaultConstruction;
        this.SystemPromptText = systemPromptText;
        this.UserPromptText = userPromptText;
    }

    /// <summary>
    ///     Gets the key of the stage for which the evidence is recorded.
    /// </summary>
    public string StageKey { get; }

    /// <summary>
    ///     Gets the name of the prompt experiment variant.
    /// </summary>
    public string VariantName { get; }

    /// <summary>
    ///     Gets the composition mode of the prompt experiment.
    /// </summary>
    public PromptCompositionMode CompositionMode { get; }

    /// <summary>
    ///     Gets a value indicating whether the default construction was used for the prompt in the executed stage.
    ///     This is true if no variant was defined for the stage and prompt role, or if the composition mode is Default and the variant content is empty, resulting in
    ///     the use of the default prompt from the fixture.
    ///     This information is important for interpreting the evidence and understanding whether the prompt was modified by the experiment or not.
    /// </summary>
    public bool UsedDefaultConstruction { get; }

    /// <summary>
    ///     Gets the system prompt text, if any.
    /// </summary>
    public string? SystemPromptText { get; }

    /// <summary>
    ///     Gets the user prompt text, if any.
    /// </summary>
    public string? UserPromptText { get; }
}
