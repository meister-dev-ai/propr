// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Runner.Execution;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     What a review may ask about the license on a host that has none. The carried capability answers, and
///     anything else fails, because a guessed answer either enables an unlicensed feature or disables a
///     licensed one, which is the same unreported-null defect class in both directions.
/// </summary>
public sealed class ManifestLicensingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheCarriedCapability_AnswersWhatDispatchResolved(bool licensed)
    {
        var manifest = RunnerManifests.Sample() with { ParallelReviewExecutionLicensed = licensed };

        var enabled = await new ManifestLicensing(manifest).IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution);

        Assert.Equal(licensed, enabled);
    }

    // An older control plane resolved nothing; the review behaves exactly as it did before the field
    // existed, which was unclamped.
    [Fact]
    public async Task AManifestWithoutTheCapability_ReadsAsLicensed()
    {
        var enabled = await new ManifestLicensing(RunnerManifests.Sample())
            .IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution);

        Assert.True(enabled);
    }

    [Fact]
    public async Task AnUncarriedCapability_FailsLoudlyRatherThanGuessing()
    {
        var licensing = new ManifestLicensing(RunnerManifests.Sample());

        var error = await Assert.ThrowsAsync<NotSupportedException>(async () => await licensing.IsEnabledAsync(PremiumCapabilityKey.SsoAuthentication));

        Assert.Contains(PremiumCapabilityKey.SsoAuthentication, error.Message, StringComparison.Ordinal);
    }
}
