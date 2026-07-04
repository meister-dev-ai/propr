// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution.Models;

/// <summary>
///     Tests for <see cref="MultiPassDiversity.ResolveResamplePasses" />: resampling routes every resample pass to
///     the diversity default model; cross-model spreads the resamples across the declared arm models (expanded by
///     their count, cycling when there are more passes than arms), and a null arm model falls back through the
///     diversity default to the tier model so a pass never loses its model.
/// </summary>
public sealed class MultiPassDiversityTests
{
    [Fact]
    public void Resampling_RoutesEveryResampleToDefaultModel()
    {
        var diversity = new MultiPassDiversity(DefaultModel: "gpt-5.4");

        var plan = diversity.ResolveResamplePasses(2, "gpt-5.3-codex");

        Assert.Equal(2, plan.Count);
        Assert.All(plan, arm => Assert.Equal("gpt-5.4", arm.ModelId));
        Assert.All(plan, arm => Assert.Equal("resampling", arm.Label));
    }

    [Fact]
    public void Resampling_WithoutDefaultModel_FallsBackToTierModel()
    {
        var diversity = new MultiPassDiversity(DefaultModel: null);

        var plan = diversity.ResolveResamplePasses(2, "gpt-5.3-codex");

        Assert.All(plan, arm => Assert.Equal("gpt-5.3-codex", arm.ModelId));
    }

    [Fact]
    public void CrossModel_SpreadsResamplesAcrossArmModels()
    {
        var diversity = new MultiPassDiversity(
            MultiPassDiversityMode.CrossModel,
            Arms:
            [
                new MultiPassArm("gpt-5.4", "gpt-5.4"),
                new MultiPassArm("mini", "gpt-5.4-mini"),
            ]);

        var plan = diversity.ResolveResamplePasses(2, "gpt-5.3-codex");

        Assert.Equal(new[] { "gpt-5.4", "gpt-5.4-mini" }, plan.Select(arm => arm.ModelId).ToArray());
        Assert.Equal(new[] { "gpt-5.4", "mini" }, plan.Select(arm => arm.Label).ToArray());
    }

    [Fact]
    public void CrossModel_ExpandsArmCountsThenCyclesToFillPasses()
    {
        var diversity = new MultiPassDiversity(
            MultiPassDiversityMode.CrossModel,
            Arms:
            [
                new MultiPassArm("gpt-5.4", "gpt-5.4", Count: 2),
                new MultiPassArm("codex", "gpt-5.3-codex"),
            ]);

        // Four resample passes over a three-entry expanded plan (gpt-5.4, gpt-5.4, codex) cycle back to the first.
        var plan = diversity.ResolveResamplePasses(4, "tier-model");

        Assert.Equal(
            new[] { "gpt-5.4", "gpt-5.4", "gpt-5.3-codex", "gpt-5.4" },
            plan.Select(arm => arm.ModelId).ToArray());
    }

    [Fact]
    public void CrossModel_ArmWithoutModel_FallsBackThroughDefaultThenTier()
    {
        var diversity = new MultiPassDiversity(
            MultiPassDiversityMode.CrossModel,
            "default-model",
            Arms: [new MultiPassArm("lens-only", null)]);

        var withDefault = diversity.ResolveResamplePasses(1, "tier-model");
        Assert.Equal("default-model", withDefault[0].ModelId);

        var noDefault = diversity with { DefaultModel = null };
        var withTier = noDefault.ResolveResamplePasses(1, "tier-model");
        Assert.Equal("tier-model", withTier[0].ModelId);
    }

    [Fact]
    public void CrossModel_WithoutArms_DegradesToDefaultModel()
    {
        var diversity = new MultiPassDiversity(MultiPassDiversityMode.CrossModel, "gpt-5.4", Arms: null);

        var plan = diversity.ResolveResamplePasses(2, "tier-model");

        Assert.All(plan, arm => Assert.Equal("gpt-5.4", arm.ModelId));
        Assert.All(plan, arm => Assert.Equal("cross-model", arm.Label));
    }

    [Fact]
    public void ResolveResamplePasses_ZeroOrFewer_ReturnsEmpty()
    {
        Assert.Empty(MultiPassDiversity.Default.ResolveResamplePasses(0, "m"));
        Assert.Empty(MultiPassDiversity.Default.ResolveResamplePasses(-1, "m"));
    }

    // Contract test: a cross-model arm must be expressible entirely in the offline eval config JSON. The eval
    // harness deserializes the config with Web defaults (camelCase, case-insensitive) plus a camelCase string enum
    // converter, so the mode string and arm list must bind through to a runnable cross-model diversity.
    [Fact]
    public void CrossModelArm_RoundTripsFromEvalConfigJson()
    {
        const string json = """
                            {
                              "configurationId": "cross-model-arm",
                              "enableMultiPassUnion": true,
                              "multiPassUnionPassCount": 3,
                              "multiPassDiversity": {
                                "mode": "crossModel",
                                "defaultModel": "gpt-5.4",
                                "resampleTemperature": 0.5,
                                "arms": [
                                  { "label": "gpt-5.4", "modelId": "gpt-5.4" },
                                  { "label": "codex", "modelId": "gpt-5.3-codex" }
                                ]
                              }
                            }
                            """;

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        var config = JsonSerializer.Deserialize<EvaluationConfiguration>(json, options);

        Assert.NotNull(config);
        Assert.True(config!.EnableMultiPassUnion);
        Assert.Equal(3, config.MultiPassUnionPassCount);
        var diversity = config.MultiPassDiversity;
        Assert.NotNull(diversity);
        Assert.Equal(MultiPassDiversityMode.CrossModel, diversity!.Mode);

        // The parsed arms drive the runtime routing: two resample passes span the two declared models.
        var plan = diversity.ResolveResamplePasses(2, "gpt-5.3-codex");
        Assert.Equal(new[] { "gpt-5.4", "gpt-5.3-codex" }, plan.Select(arm => arm.ModelId).ToArray());
    }
}
