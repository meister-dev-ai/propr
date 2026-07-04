// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Selects the source of diversity for multi-pass union generation. The k-pass source is a configuration
///     arm of a single mechanism, so security- and PR-level lens arms slot in without new orchestration.
/// </summary>
public enum MultiPassDiversityMode
{
    /// <summary>
    ///     k independent passes over the same file with the same model at a resampling temperature. The default,
    ///     and the only arm wired into the product runtime today.
    /// </summary>
    Resampling = 0,

    /// <summary>
    ///     Passes split across models to surface disjoint finds. Design-only arm; not wired into the runtime.
    /// </summary>
    CrossModel = 1,

    /// <summary>
    ///     One or more passes run a different prompt/scope lens rather than a resample. Design-only arm; not wired
    ///     into the runtime.
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
    string? DefaultModel = "gpt-5.4",
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
}
