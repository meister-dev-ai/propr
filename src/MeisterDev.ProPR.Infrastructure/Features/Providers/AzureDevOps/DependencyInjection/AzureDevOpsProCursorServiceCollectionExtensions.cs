// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Broker;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.ProCursor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;

/// <summary>Registers the native compatibility backend for bounded SCM materialization.</summary>
public static class AzureDevOpsProCursorServiceCollectionExtensions
{
    public static IServiceCollection AddAzureDevOpsProCursorBroker(this IServiceCollection services)
    {
        services.TryAddScoped<AdoProCursorScmBroker>();
        services.TryAddScoped<LocalProPrScmBroker>(sp => new(sp.GetRequiredService<IClientAdminService>(), sp.GetRequiredService<AdoProCursorScmBroker>()));
        return services;
    }
}
