// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.AzureDevOps.ProCursor;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeisterProPR.ProCursor.Infrastructure.AzureDevOps.DependencyInjection;

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

        services.TryAddScoped<IProCursorMaterializer, AdoRepositoryMaterializer>();
        services.TryAddScoped<IProCursorMaterializer, AdoWikiMaterializer>();
        services.TryAddScoped<IProCursorTrackedBranchChangeDetector, AdoTrackedBranchChangeDetector>();

        return services;
    }
}
