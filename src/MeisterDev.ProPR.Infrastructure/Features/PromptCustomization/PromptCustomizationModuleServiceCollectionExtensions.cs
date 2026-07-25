// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.PromptCustomization.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.PromptCustomization.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Features.PromptCustomization;

/// <summary>
///     Extension methods for registering the Prompt Customization module.
/// </summary>
public static class PromptCustomizationModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers prompt override persistence and application services.
    /// </summary>
    public static IServiceCollection AddPromptCustomizationModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        if (configuration.HasDatabaseConnectionString())
        {
            services.AddScoped<IPromptOverrideRepository, PromptOverrideRepository>();
        }

        services.AddScoped<IPromptOverrideService, PromptOverrideService>();

        return services;
    }
}
