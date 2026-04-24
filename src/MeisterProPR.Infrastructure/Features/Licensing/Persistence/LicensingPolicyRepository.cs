// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>EF Core-backed installation licensing policy store.</summary>
public sealed class LicensingPolicyRepository(
    MeisterProPRDbContext dbContext,
    IPremiumCapabilityCatalog capabilityCatalog) : ILicensingPolicyStore
{
    private const int SingletonPolicyId = 1;

    public async Task<InstallationLicensingPolicy> GetAsync(CancellationToken cancellationToken = default)
    {
        await this.EnsureSeededAsync(cancellationToken);

        var editionRecord = await dbContext.InstallationEditions
            .AsNoTracking()
            .SingleAsync(record => record.Id == SingletonPolicyId, cancellationToken);
        var overrides = await dbContext.PremiumCapabilityOverrides
            .AsNoTracking()
            .ToDictionaryAsync(
                record => record.CapabilityKey,
                record => record.OverrideState,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);

        return ToPolicy(editionRecord, overrides);
    }

    public async Task<InstallationLicensingPolicy> UpdateAsync(
        InstallationEdition edition,
        IReadOnlyCollection<CapabilityOverrideMutation> capabilityOverrides,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilityOverrides);

        await this.EnsureSeededAsync(cancellationToken);

        var editionRecord = await dbContext.InstallationEditions
            .SingleAsync(record => record.Id == SingletonPolicyId, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        editionRecord.Edition = edition;
        editionRecord.UpdatedAt = now;
        editionRecord.UpdatedByUserId = actorUserId;

        if (edition == InstallationEdition.Commercial)
        {
            editionRecord.ActivatedAt ??= now;
            editionRecord.ActivatedByUserId ??= actorUserId;
        }
        else
        {
            editionRecord.ActivatedAt = null;
            editionRecord.ActivatedByUserId = null;
        }

        foreach (var overrideMutation in capabilityOverrides)
        {
            if (capabilityCatalog.Get(overrideMutation.Key) is null)
            {
                throw new KeyNotFoundException($"Unknown premium capability '{overrideMutation.Key}'.");
            }

            var existingRecord = await dbContext.PremiumCapabilityOverrides
                .FirstOrDefaultAsync(
                    record => record.CapabilityKey == overrideMutation.Key,
                    cancellationToken);

            if (overrideMutation.OverrideState == PremiumCapabilityOverrideState.Default)
            {
                if (existingRecord is not null)
                {
                    dbContext.PremiumCapabilityOverrides.Remove(existingRecord);
                }

                continue;
            }

            if (existingRecord is null)
            {
                dbContext.PremiumCapabilityOverrides.Add(
                    new PremiumCapabilityOverrideRecord
                    {
                        CapabilityKey = overrideMutation.Key,
                        OverrideState = overrideMutation.OverrideState,
                        UpdatedAt = now,
                        UpdatedByUserId = actorUserId,
                    });
                continue;
            }

            existingRecord.OverrideState = overrideMutation.OverrideState;
            existingRecord.UpdatedAt = now;
            existingRecord.UpdatedByUserId = actorUserId;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return await this.GetAsync(cancellationToken);
    }

    private async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        if (await dbContext.InstallationEditions.AnyAsync(cancellationToken))
        {
            return;
        }

        dbContext.InstallationEditions.Add(
            new InstallationEditionRecord
            {
                Id = SingletonPolicyId,
                Edition = InstallationEdition.Community,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static InstallationLicensingPolicy ToPolicy(
        InstallationEditionRecord editionRecord,
        IReadOnlyDictionary<string, PremiumCapabilityOverrideState> overrides)
    {
        return new InstallationLicensingPolicy(
            editionRecord.Edition,
            editionRecord.ActivatedAt,
            editionRecord.ActivatedByUserId,
            editionRecord.UpdatedAt,
            editionRecord.UpdatedByUserId,
            overrides);
    }
}
