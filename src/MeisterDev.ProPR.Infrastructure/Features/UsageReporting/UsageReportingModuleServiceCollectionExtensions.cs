// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Features.UsageReporting;

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

            // Model calls that happen outside a review job have no protocol to count their tokens. This records
            // them onto the same daily per-client usage row the review path writes, which is what a cost report
            // reads and what a client budget cap is measured against. Registered here because that row is this
            // module's, and because more than one feature now needs it.
            services.AddScoped<IModelUsageRecorder, ModelUsageRecorder>();
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
