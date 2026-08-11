// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Services;

/// <summary>
///     Checks the two timers that have to agree with each other.
///     <para>
///         Each is individually sensible and their ranges are validated separately, which is why the bad
///         combination gets through: the active-heartbeat window decides when a runner stops counting as
///         capacity, and the lease duration decides when its work is taken back. A window
///         longer than the lease opens a gap where a runner's jobs are already being reclaimed while the
///         control plane still counts it as alive, so nothing runs the work: not the runner, whose leases
///         keep expiring, and not the control plane, which is waiting for a fleet it treats as healthy.
///     </para>
///     <para>
///         Refused at startup rather than logged, because the symptom is a queue that stops moving for as
///         long as the window is set to, and nothing about that points at these two settings.
///     </para>
/// </summary>
public sealed class RunnerFleetOptionsValidator(IOptions<ReviewLeaseOptions> leaseOptions)
    : IValidateOptions<RunnerFleetOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RunnerFleetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var leaseDuration = leaseOptions.Value.LeaseDuration;
        if (options.ActiveHeartbeatWindow > leaseDuration)
        {
            return ValidateOptionsResult.Fail(
                $"RUNNER_ACTIVE_HEARTBEAT_WINDOW_SECONDS ({options.ActiveHeartbeatWindowSeconds}) must not exceed "
                + $"REVIEW_LEASE_DURATION_SECONDS ({leaseOptions.Value.LeaseDurationSeconds}). A runner counted as "
                + "available for longer than its leases survive leaves work with nobody to run it: its own leases "
                + "keep being reclaimed, and the control plane keeps waiting for a fleet it believes is healthy.");
        }

        return ValidateOptionsResult.Success;
    }
}
