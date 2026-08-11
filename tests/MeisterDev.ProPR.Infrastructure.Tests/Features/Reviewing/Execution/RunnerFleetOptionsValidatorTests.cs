// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Services;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     The one rule that spans two option sets, which is why neither one's own range check catches it.
///     Each setting is individually sensible; the damage is in the combination.
/// </summary>
public sealed class RunnerFleetOptionsValidatorTests
{
    [Fact]
    public void TheShippedDefaults_AreAccepted()
    {
        Assert.True(Validate(activeWindowSeconds: 120, leaseDurationSeconds: 120).Succeeded);
    }

    [Fact]
    public void AWindowShorterThanTheLease_IsAccepted()
    {
        Assert.True(Validate(activeWindowSeconds: 60, leaseDurationSeconds: 300).Succeeded);
    }

    // The failure the rule exists for: a runner counted as available for an hour while its leases expire
    // every thirty seconds. Its own work keeps being reclaimed, and the control plane keeps waiting for a
    // fleet it treats as healthy, so nothing runs the queue for as long as the window is set to.
    [Fact]
    public void AWindowLongerThanTheLease_IsRefusedAtStartup()
    {
        var result = Validate(activeWindowSeconds: 3600, leaseDurationSeconds: 30);

        Assert.True(result.Failed);
        Assert.Contains("RUNNER_ACTIVE_HEARTBEAT_WINDOW_SECONDS", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("REVIEW_LEASE_DURATION_SECONDS", result.FailureMessage, StringComparison.Ordinal);
    }

    // Both numbers appear in the message. A refusal naming only the rule leaves an operator comparing two
    // environment variables by hand to find which one they set.
    [Fact]
    public void TheRefusal_NamesBothValues()
    {
        var result = Validate(activeWindowSeconds: 600, leaseDurationSeconds: 120);

        Assert.Contains("600", result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("120", result.FailureMessage, StringComparison.Ordinal);
    }

    private static ValidateOptionsResult Validate(int activeWindowSeconds, int leaseDurationSeconds)
    {
        var validator = new RunnerFleetOptionsValidator(
            Microsoft.Extensions.Options.Options.Create(new ReviewLeaseOptions { LeaseDurationSeconds = leaseDurationSeconds }));

        return validator.Validate(
            Microsoft.Extensions.Options.Options.DefaultName,
            new RunnerFleetOptions { ActiveHeartbeatWindowSeconds = activeWindowSeconds });
    }
}
