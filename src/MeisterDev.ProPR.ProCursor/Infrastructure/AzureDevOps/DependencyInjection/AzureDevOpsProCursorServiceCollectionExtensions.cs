// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.AzureDevOps.ProCursor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterDev.ProPR.ProCursor.Infrastructure.AzureDevOps.DependencyInjection;

internal static class AzureDevOpsProCursorServiceCollectionExtensions
{
    public static IServiceCollection AddAzureDevOpsProCursorServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (configuration.GetValue<bool>("ADO_STUB_PR"))
        {
            services.TryAddScoped<IProCursorTrackedBranchChangeDetector, NullProCursorTrackedBranchChangeDetector>();
            return services;
        }

        // Materializers are consumed as IEnumerable<IProCursorMaterializer> and selected by source kind,
        // so every implementation has to reach the container. TryAddScoped keys on the service type alone
        // and would silently drop every registration after the first, leaving the later source kinds with
        // no materializer at all; TryAddEnumerable de-duplicates by implementation type instead.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProCursorMaterializer, AdoRepositoryMaterializer>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IProCursorMaterializer, AdoWikiMaterializer>());
        services.TryAddScoped<IProCursorTrackedBranchChangeDetector, AdoTrackedBranchChangeDetector>();

        return services;
    }
}
