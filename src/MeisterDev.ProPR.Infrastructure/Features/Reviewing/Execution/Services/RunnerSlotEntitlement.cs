// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Services;

/// <summary>
///     Meters how many runners may hold leases at once.
///     <para>
///         Enforced here rather than in the runner, because a check inside the artifact a customer hosts is
///         the easiest one to remove. A runner cannot create its own work either, so the moment a lease is
///         offered is the only place the count has to be right.
///     </para>
///     <para>
///         Until signed entitlements land this is an operational guardrail and an audit measure, not
///         tamper-proof enforcement: the number it reads is one an operator with database access can change.
///         That is why an unset count means unmetered rather than zero. Reading "no value" as "no slots"
///         would refuse every lease on an installation that never opted into metering, which is a worse
///         failure than not metering it.
///     </para>
/// </summary>
public sealed class RunnerSlotEntitlement(
    MeisterProPRDbContext dbContext,
    IRunnerLeaseOfferStore offers,
    ILicensingCapabilityService? licensing = null) : IRunnerSlotEntitlement
{
    /// <inheritdoc />
    public async Task<RunnerSlotAdmission> AdmitAsync(Guid runnerId, CancellationToken ct = default)
    {
        if (licensing is not null
            && !await licensing.IsEnabledAsync(PremiumCapabilityKey.DistributedExecution, ct))
        {
            return new RunnerSlotAdmission(
                RunnerLeaseRefusal.NotLicensed,
                "Distributed review execution is not licensed for this installation.");
        }

        var entitled = await dbContext.InstallationEditions
            .AsNoTracking()
            .Select(e => e.EntitledRunnerSlots)
            .FirstOrDefaultAsync(ct);

        // Null is unmetered; zero is zero. Folding them together made an explicit "no slots" entitlement
        // admit every runner, which is the opposite of what setting it to zero asks for. A negative value
        // is malformed rather than meaningful, so it is treated as no slots too rather than as unmetered,
        // which would let bad data silently disable metering.
        if (entitled is null)
        {
            return RunnerSlotAdmission.Admitted;
        }

        if (entitled.Value <= 0)
        {
            return new RunnerSlotAdmission(
                RunnerLeaseRefusal.SlotLimitReached,
                "This installation is entitled to no runner slots.");
        }

        // Counted by runner rather than by leased job, so a runner given several jobs consumes one slot.
        // Counting jobs would meter throughput instead of capacity and would make the entitlement mean
        // something different on an installation that raised its per-runner concurrency.
        var held = await offers.CountRunnersHoldingLeasesAsync(ct);

        // A runner already holding a lease is inside the count, so letting it take another job is free.
        // Refusing it would cap a runner at one job however many slots the installation has.
        if (held < entitled.Value || await this.AlreadyHoldsALeaseAsync(runnerId, ct))
        {
            return RunnerSlotAdmission.Admitted;
        }

        return new RunnerSlotAdmission(
            RunnerLeaseRefusal.SlotLimitReached,
            $"All {entitled.Value} entitled runner slots are in use.");
    }

    private async Task<bool> AlreadyHoldsALeaseAsync(Guid runnerId, CancellationToken ct)
    {
        var owner = runnerId.ToString("D");
        return await dbContext.ReviewJobs
            .AsNoTracking()
            .AnyAsync(j => j.LeaseOwner == owner && j.LeaseExpiresAt > DateTimeOffset.UtcNow, ct);
    }
}
