// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeisterProPR.Infrastructure.Features.Clients;

/// <summary>
///     Extension methods for registering the Clients module.
/// </summary>
public static class ClientsModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers client administration and AI connection services.
    /// </summary>
    public static IServiceCollection AddClientsModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        if (configuration.HasDatabaseConnectionString())
        {
            services.AddScoped<IClientRegistry, DbClientRegistry>();
            services.AddScoped<IClientAdminService, ClientAdminService>();
            services.AddScoped<IAiConnectionRepository, AiConnectionRepository>();
        }

        return services;
    }
}
