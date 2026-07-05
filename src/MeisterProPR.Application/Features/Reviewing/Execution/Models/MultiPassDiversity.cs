// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Selects the source of diversity for the eval-harness multi-pass union path. The k-pass source is a
///     configuration arm of a single mechanism, so security- and PR-level lens arms slot in without new
///     orchestration. Production reviews instead drive the extra passes from the per-client review-pass list.
/// </summary>
public enum MultiPassDiversityMode
{
    /// <summary>
    ///     k independent passes over the same file with the same model at a resampling temperature. The per-arm and
    ///     default model ids are eval-harness-only overrides; when they resolve to null the harness reuses the tier
    ///     model.
    /// </summary>
    Resampling = 0,

    /// <summary>
    ///     Passes split across models to surface disjoint finds. The per-arm/default model ids are eval-harness-only
    ///     overrides that route each resample to its own model.
    /// </summary>
    CrossModel = 1,

    /// <summary>
    ///     One or more passes run a different prompt/scope lens rather than a resample. Each declared arm carries a
    ///     <see cref="MultiPassArm.Lens" /> (e.g. <c>security</c>); the eval fan-out scopes a lens pass to the files
    ///     that lens targets (the security lens runs on security-floor-flagged files, any tier).
    /// </summary>
    Lens = 2,
}

/// <summary>
///     One arm of a multi-pass diversity configuration: an optional per-arm model override, an optional lens
///     identifier, a label recorded in provenance, and the number of passes the arm contributes.
/// </summary>
public sealed record MultiPassArm(
    string Label,
    string? ModelId = null,
    string? Lens = null,
    int Count = 1);

/// <summary>
///     Configuration for multi-pass union diversity. The default is resampling with a fixed resample temperature;
///     alternative arms (cross-model, lens) are represented here so the offline harness can sweep them.
/// </summary>
public sealed record MultiPassDiversity(
    MultiPassDiversityMode Mode = MultiPassDiversityMode.Resampling,
    string? DefaultModel = null,
    float ResampleTemperature = 0.5f,
    IReadOnlyList<MultiPassArm>? Arms = null)
{
    /// <summary>Default resampling diversity used when no explicit configuration is supplied.</summary>
    public static MultiPassDiversity Default { get; } = new();

    /// <summary>Configured arms, or an empty list when none are declared.</summary>
    public IReadOnlyList<MultiPassArm> ArmsOrEmpty => this.Arms ?? [];

    /// <summary>
    ///     The provenance label recorded for a resample pass under this diversity configuration.
    /// </summary>
    public string ResolveArmLabel()
    {
        return this.Mode switch
        {
            MultiPassDiversityMode.Resampling => "resampling",
            MultiPassDiversityMode.CrossModel => "cross-model",
            MultiPassDiversityMode.Lens => "lens",
            _ => "resampling",
        };
    }

    /// <summary>
    ///     Resolves the model + provenance label for each resample pass (passes 2..k; the baseline pass keeps the
    ///     file's tier model) on the eval-harness path. Resampling uses the diversity default model for every
    ///     resample; cross-model spreads the resamples across the declared arms (expanded by their count, cycling
    ///     when there are more passes than arms). A resolved arm's <see cref="MultiPassArm.ModelId" /> is an
    ///     eval-harness-only model override; when it resolves to null the harness reuses the file's tier model.
    /// </summary>
    /// <param name="resamplePassCount">The number of resample passes to plan (k - 1).</param>
    public IReadOnlyList<MultiPassArm> ResolveResamplePasses(int resamplePassCount)
    {
        if (resamplePassCount <= 0)
        {
            return [];
        }

        var result = new List<MultiPassArm>(resamplePassCount);

        // Arm-driven modes spread the resamples across the declared arms (expanded by their count, cycling when
        // there are more passes than arms), preserving each arm's model and lens. Cross-model routes each resample
        // to its own model; the lens mode routes each resample to its own prompt/scope lens (e.g. a security arm).
        if (this.Mode is MultiPassDiversityMode.CrossModel or MultiPassDiversityMode.Lens)
        {
            var plan = this.ExpandArms();
            for (var i = 0; i < resamplePassCount; i++)
            {
                if (plan.Count > 0)
                {
                    var arm = plan[i % plan.Count];
                    result.Add(arm with { ModelId = arm.ModelId ?? this.DefaultModel });
                }
                else
                {
                    // An arm-driven mode with no arms declared degrades to the default model (behaves like resampling).
                    result.Add(new MultiPassArm(this.ResolveArmLabel(), this.DefaultModel));
                }
            }

            return result;
        }

        // Resampling: every resample runs the diversity default model (eval-harness override) or, when that is
        // null, the file's tier model reused by the harness.
        var label = this.ResolveArmLabel();
        for (var i = 0; i < resamplePassCount; i++)
        {
            result.Add(new MultiPassArm(label, this.DefaultModel));
        }

        return result;
    }

    private IReadOnlyList<MultiPassArm> ExpandArms()
    {
        if (this.Arms is null || this.Arms.Count == 0)
        {
            return [];
        }

        var expanded = new List<MultiPassArm>(this.Arms.Count);
        foreach (var arm in this.Arms)
        {
            var count = Math.Max(1, arm.Count);
            for (var i = 0; i < count; i++)
            {
                expanded.Add(arm);
            }
        }

        return expanded;
    }
}
