// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;

namespace MeisterProPR.Infrastructure.Features.Licensing.FeatureManagement;

/// <summary>Builds feature-management definitions from the persisted licensing capability state.</summary>
public sealed class PersistedFeatureDefinitionProvider(
    IServiceScopeFactory scopeFactory,
    IPremiumCapabilityCatalog capabilityCatalog) : IFeatureDefinitionProvider
{
    public async Task<FeatureDefinition> GetFeatureDefinitionAsync(string featureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);

        if (capabilityCatalog.Get(featureName) is null)
        {
            return null!;
        }

        using var scope = scopeFactory.CreateScope();
        var licensingCapabilityService = scope.ServiceProvider.GetRequiredService<ILicensingCapabilityService>();
        var snapshot = await licensingCapabilityService.GetCapabilityAsync(featureName);

        return new FeatureDefinition
        {
            Name = snapshot.Key,
            Status = snapshot.IsAvailable ? FeatureStatus.Conditional : FeatureStatus.Disabled,
            EnabledFor = snapshot.IsAvailable
                ? [new FeatureFilterConfiguration { Name = LicensedCapabilityFeatureFilter.FilterAliasName }]
                : [],
        };
    }

    public async IAsyncEnumerable<FeatureDefinition> GetAllFeatureDefinitionsAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var licensingCapabilityService = scope.ServiceProvider.GetRequiredService<ILicensingCapabilityService>();

        foreach (var capability in capabilityCatalog.GetAll())
        {
            var snapshot = await licensingCapabilityService.GetCapabilityAsync(capability.Key);
            yield return new FeatureDefinition
            {
                Name = snapshot.Key,
                Status = snapshot.IsAvailable ? FeatureStatus.Conditional : FeatureStatus.Disabled,
                EnabledFor = snapshot.IsAvailable
                    ? [new FeatureFilterConfiguration { Name = LicensedCapabilityFeatureFilter.FilterAliasName }]
                    : [],
            };
        }
    }
}
