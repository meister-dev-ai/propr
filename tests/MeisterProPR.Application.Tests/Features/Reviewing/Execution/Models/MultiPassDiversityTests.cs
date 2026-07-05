// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution.Models;

/// <summary>
///     Tests for <see cref="MultiPassDiversity.ResolveResamplePasses" /> (the eval-harness multi-pass path):
///     resampling routes every resample pass to the diversity default model; cross-model spreads the resamples
///     across the declared arm models (expanded by their count, cycling when there are more passes than arms). An
///     arm model id is an eval-harness-only override; when it resolves to null the harness reuses the file's tier
///     model.
/// </summary>
public sealed class MultiPassDiversityTests
{
    private static readonly string[] Gpt54AndGpt54MiniModelIds = ["gpt-5.4", "gpt-5.4-mini"];
    private static readonly string[] Gpt54AndMiniLabels = ["gpt-5.4", "mini"];
    private static readonly string[] Gpt54TwiceCodexThenGpt54ModelIds = ["gpt-5.4", "gpt-5.4", "gpt-5.3-codex", "gpt-5.4"];
    private static readonly string[] Gpt54AndGpt53CodexModelIds = ["gpt-5.4", "gpt-5.3-codex"];

    private static readonly JsonSerializerOptions EvalConfigJsonOptions = BuildEvalConfigJsonOptions();

    private static JsonSerializerOptions BuildEvalConfigJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    [Fact]
    public void Resampling_RoutesEveryResampleToDefaultModel()
    {
        var diversity = new MultiPassDiversity(DefaultModel: "gpt-5.4");

        var plan = diversity.ResolveResamplePasses(2);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, arm => Assert.Equal("gpt-5.4", arm.ModelId));
        Assert.All(plan, arm => Assert.Equal("resampling", arm.Label));
    }

    [Fact]
    public void Resampling_WithoutDefaultModel_LeavesModelNull()
    {
        // No config model ⇒ null model id ⇒ the eval harness reuses the file's tier model for the resample.
        var diversity = new MultiPassDiversity(DefaultModel: null);

        var plan = diversity.ResolveResamplePasses(2);

        Assert.All(plan, arm => Assert.Null(arm.ModelId));
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

        var plan = diversity.ResolveResamplePasses(2);

        Assert.Equal(Gpt54AndGpt54MiniModelIds, plan.Select(arm => arm.ModelId).ToArray());
        Assert.Equal(Gpt54AndMiniLabels, plan.Select(arm => arm.Label).ToArray());
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
        var plan = diversity.ResolveResamplePasses(4);

        Assert.Equal(Gpt54TwiceCodexThenGpt54ModelIds, plan.Select(arm => arm.ModelId).ToArray());
    }

    [Fact]
    public void CrossModel_ArmWithoutModel_FallsBackToDefaultThenNull()
    {
        var diversity = new MultiPassDiversity(
            MultiPassDiversityMode.CrossModel,
            "default-model",
            Arms: [new MultiPassArm("lens-only")]);

        var withDefault = diversity.ResolveResamplePasses(1);
        Assert.Equal("default-model", withDefault[0].ModelId);

        // Without a default model the pass model is left null so the eval harness reuses the file's tier model.
        var noDefault = diversity with { DefaultModel = null };
        var withoutDefault = noDefault.ResolveResamplePasses(1);
        Assert.Null(withoutDefault[0].ModelId);
    }

    [Fact]
    public void CrossModel_WithoutArms_DegradesToDefaultModel()
    {
        var diversity = new MultiPassDiversity(MultiPassDiversityMode.CrossModel, "gpt-5.4", Arms: null);

        var plan = diversity.ResolveResamplePasses(2);

        Assert.All(plan, arm => Assert.Equal("gpt-5.4", arm.ModelId));
        Assert.All(plan, arm => Assert.Equal("cross-model", arm.Label));
    }

    [Fact]
    public void ResolveResamplePasses_ZeroOrFewer_ReturnsEmpty()
    {
        Assert.Empty(MultiPassDiversity.Default.ResolveResamplePasses(0));
        Assert.Empty(MultiPassDiversity.Default.ResolveResamplePasses(-1));
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

        var options = EvalConfigJsonOptions;

        var config = JsonSerializer.Deserialize<EvaluationConfiguration>(json, options);

        Assert.NotNull(config);
        Assert.True(config!.EnableMultiPassUnion);
        Assert.Equal(3, config.MultiPassUnionPassCount);
        var diversity = config.MultiPassDiversity;
        Assert.NotNull(diversity);
        Assert.Equal(MultiPassDiversityMode.CrossModel, diversity!.Mode);

        // The parsed arms drive the runtime routing: two resample passes span the two declared models.
        var plan = diversity.ResolveResamplePasses(2);
        Assert.Equal(Gpt54AndGpt53CodexModelIds, plan.Select(arm => arm.ModelId).ToArray());
    }
}
