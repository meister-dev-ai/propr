// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
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
    public static IServiceCollection AddUsageReportingModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        var isManagedRemoteMode = new ProCursorRemoteOptions
        {
            Mode = configuration["PROCURSOR_REMOTE_MODE"],
            ServiceBaseUrl = configuration["PROCURSOR_SERVICE_BASE_URL"],
            SharedKey = configuration["PROCURSOR_SHARED_KEY"],
        }.IsRemoteEnabled;

        if (configuration.HasDatabaseConnectionString())
        {
            services.AddScoped<IClientTokenUsageRepository, ClientTokenUsageRepository>();
        }

        if (isManagedRemoteMode)
        {
            services.AddScoped<IProCursorTokenUsageReadRepository>(sp =>
                sp.GetRequiredService<RemoteProCursorTokenUsageReadRepository>());
            services.AddScoped<IProCursorTokenUsageRebuildService>(sp =>
                sp.GetRequiredService<RemoteProCursorTokenUsageRebuildService>());
        }

        return services;
    }
}
