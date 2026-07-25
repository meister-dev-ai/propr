// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Commands.UpdateLicensing;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Queries.GetLicensingSummary;
using MeisterDev.ProPR.Application.Features.Licensing.Services;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.FeatureManagement;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing;

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
