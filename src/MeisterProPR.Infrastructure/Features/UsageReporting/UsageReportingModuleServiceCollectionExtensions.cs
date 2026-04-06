// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeisterProPR.Infrastructure.Features.UsageReporting;

/// <summary>
///     Extension methods for registering the Usage Reporting module.
/// </summary>
public static class UsageReportingModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers token usage persistence and aggregation services.
    /// </summary>
    public static IServiceCollection AddUsageReportingModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        if (configuration.HasDatabaseConnectionString())
        {
            services.AddScoped<IClientTokenUsageRepository, ClientTokenUsageRepository>();
            services.AddSingleton<IProCursorTokenUsageRecorder, EfProCursorTokenUsageRecorder>();
            services.AddScoped<IProCursorTokenUsageReadRepository, ProCursorTokenUsageReadRepository>();
            services.AddScoped<IProCursorTokenUsageAggregationService, MeisterProPR.Infrastructure.Services.ProCursorTokenUsageAggregationService>();
            services.AddScoped<IProCursorTokenUsageRebuildService, MeisterProPR.Infrastructure.Services.ProCursorTokenUsageRebuildService>();
            services.AddScoped<IProCursorTokenUsageRetentionService, MeisterProPR.Infrastructure.Services.ProCursorTokenUsageRetentionService>();
        }

        return services;
    }
}
