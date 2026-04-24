// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Commands.UpdateLicensing;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Licensing.Queries.GetLicensingSummary;
using MeisterProPR.Application.Features.Licensing.Services;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Licensing.FeatureManagement;
using MeisterProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterProPR.Infrastructure.Features.Licensing.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;

namespace MeisterProPR.Infrastructure.Features.Licensing;

/// <summary>Registers installation licensing services and feature-management integration.</summary>
public static class LicensingModuleServiceCollectionExtensions
{
    /// <summary>Registers the licensing module when database-backed runtime services are available.</summary>
    public static IServiceCollection AddLicensingModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        if (!configuration.HasDatabaseConnectionString())
        {
            return services;
        }

        services.AddSingleton<IPremiumCapabilityCatalog, StaticPremiumCapabilityCatalog>();
        services.AddScoped<ILicensingPolicyStore, LicensingPolicyRepository>();
        services.AddScoped<ILicensingCapabilityService, LicensingCapabilityService>();
        services.AddScoped<GetLicensingSummaryHandler>();
        services.AddScoped<UpdateLicensingHandler>();

        services.AddFeatureManagement()
            .AddFeatureFilter<LicensedCapabilityFeatureFilter>();
        services.AddSingleton<IFeatureDefinitionProvider, PersistedFeatureDefinitionProvider>();

        return services;
    }
}
