// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     The levels a runner review takes from its manifest. Every binding of a logical model carries the level of that
///     logical model; the level a pass sets and the level of the review purpose's assignment travel apart from it.
/// </summary>
public sealed class ManifestReasoningLevelTests
{
    // Two passes on the same logical model share one binding on the executor, so each pass takes its own level from
    // the pass and not from the binding. A pass without a level of its own later falls back to the logical model.
    [Fact]
    public async Task EachPass_TakesItsOwnLevel_AndAPassWithoutOneTakesNone()
    {
        var binding = RunnerManifests.Model("reviewer", "medium");
        var manifest = RunnerManifests.Sample() with
        {
            Passes =
            [
                new RunnerReviewPass(1, binding, null, null, false),
                new RunnerReviewPass(2, binding, "security", null, false, "high"),
            ],
        };

        var context = await ManifestReasoningCaptureTests.BuildContextFromManifestAsync(manifest);

        Assert.Equal([null, "high"], context.ReviewPasses.Select(pass => pass.ReasoningLevel));
    }

    // The stages on the default model take the level of the review purpose's assignment before the level of its logical
    // model. A manifest from an older control plane carries no purpose level, and the logical model's level applies.
    [Theory]
    [InlineData("xhigh", "medium", "xhigh")]
    [InlineData(null, "medium", "medium")]
    [InlineData(null, "", null)]
    public async Task TheDefaultModel_TakesThePurposeLevelBeforeTheLevelOfItsLogicalModel(
        string? purposeLevel,
        string logicalModelLevel,
        string? expected)
    {
        var manifest = RunnerManifests.Sample() with
        {
            DefaultModel = RunnerManifests.Model("reviewer-default", logicalModelLevel) with { PurposeReasoningLevel = purposeLevel },
        };

        var context = await ManifestReasoningCaptureTests.BuildContextFromManifestAsync(manifest);

        Assert.Equal(expected, context.RuntimeCapabilities.ConfiguredReasoningLevel);
    }
}
