// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Api.Workers;
using MeisterDev.ProPR.Infrastructure.Features.PlatformAdminElevation;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Features.PlatformAdminElevation;

/// <summary>Registers the elevations of platform administrators with the API host.</summary>
public static class PlatformAdminElevationRegistration
{
    /// <summary>
    ///     Registers the elevation services, the filter that applies the content rule to every controller action, and the
    ///     worker that records expiries.
    /// </summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The configuration of the host.</param>
    /// <param name="runExpiryWorker">Whether the worker that records expiries runs; it needs the database.</param>
    public static IServiceCollection AddPlatformAdminElevation(
        this IServiceCollection services,
        IConfiguration configuration,
        bool runExpiryWorker)
    {
        services.AddPlatformAdminElevationModule(configuration);
        services.Configure<MvcOptions>(options => options.Filters.Add<PlatformAdminContentFilter>());

        services.AddSingleton<PlatformAdminElevationExpiryWorker>();
        if (runExpiryWorker)
        {
            services.AddHostedService(sp => sp.GetRequiredService<PlatformAdminElevationExpiryWorker>());
        }

        return services;
    }
}
