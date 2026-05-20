// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Offline-only prompt experiment context merged into one workflow execution.
/// </summary>
public sealed class PromptExperimentContext
{
    private readonly Dictionary<(string StageKey, PromptStageRole PromptRole), StagePromptVariant> _variants;

    /// <summary>
    ///     Create new prompt experiment context for one workflow execution.
    /// </summary>
    /// <param name="variantName">The name of the variant.</param>
    /// <param name="stageVariants">The list of stage variants.</param>
    /// <param name="skippedSteps">The offline-only hard step skips to apply alongside prompt variants.</param>
    public PromptExperimentContext(
        string variantName,
        IReadOnlyList<StagePromptVariant>? stageVariants = null,
        ReviewStepSkips? skippedSteps = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variantName);

        this.VariantName = variantName;
        this.StageVariants = stageVariants ?? Array.Empty<StagePromptVariant>();
        this.SkippedSteps = skippedSteps ?? new ReviewStepSkips();
        this.ActiveStageKeys = this.StageVariants.Select(variant => variant.StageKey).Distinct(StringComparer.Ordinal).ToArray();
        this._variants = this.StageVariants.ToDictionary(
            variant => (variant.StageKey, variant.PromptRole),
            variant => variant,
            PromptExperimentVariantKeyComparer.Instance);
    }

    /// <summary>
    ///     The name of the prompt experiment variant, e.g. "zero-shot", "few-shot", "chain-of-thought", etc. This is used for artifact metadata and can be used for
    ///     filtering and comparison of results between different variants.
    /// </summary>
    public string VariantName { get; }

    /// <summary>
    ///     The list of stage variants defined for this prompt experiment context. Each stage variant defines the prompt role (e.g. system, user, assistant) and the
    ///     content for one stage in the workflow execution.
    ///     The stage key is used to match the variant to the corresponding stage in the fixture. Note that not all stages need to have a variant,
    ///     in which case the default prompt from the fixture will be used for those stages.
    /// </summary>
    public IReadOnlyList<StagePromptVariant> StageVariants { get; }

    /// <summary>
    ///     Offline-only hard step skips to apply alongside prompt variants.
    /// </summary>
    public ReviewStepSkips SkippedSteps { get; }

    /// <summary>
    ///     The list of active stage keys for this prompt experiment context. These are the stage keys that have variants defined.
    /// </summary>
    public IReadOnlyList<string> ActiveStageKeys { get; }

    /// <summary>
    ///     Try to get the prompt variant for a given stage key and prompt role. Returns true if a variant is defined for the specified stage and role, false
    ///     otherwise.
    /// </summary>
    /// <param name="stageKey"></param>
    /// <param name="promptRole"></param>
    /// <param name="variant"></param>
    /// <returns></returns>
    public bool TryGetVariant(string stageKey, PromptStageRole promptRole, out StagePromptVariant? variant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageKey);

        if (this._variants.TryGetValue((stageKey, promptRole), out var resolved))
        {
            variant = resolved;
            return true;
        }

        variant = null;
        return false;
    }

    private sealed class PromptExperimentVariantKeyComparer : IEqualityComparer<(string StageKey, PromptStageRole PromptRole)>
    {
        public static PromptExperimentVariantKeyComparer Instance { get; } = new();

        public bool Equals((string StageKey, PromptStageRole PromptRole) x, (string StageKey, PromptStageRole PromptRole) y)
        {
            return string.Equals(x.StageKey, y.StageKey, StringComparison.Ordinal) && x.PromptRole == y.PromptRole;
        }

        public int GetHashCode((string StageKey, PromptStageRole PromptRole) obj)
        {
            return HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.StageKey), obj.PromptRole);
        }
    }
}
