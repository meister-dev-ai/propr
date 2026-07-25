// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Interfaces;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Features.Mentions;

/// <summary>
///     Extension methods for registering the Mentions module.
/// </summary>
public static class MentionsModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers mention scan, reply, and AI answer services.
    /// </summary>
    public static IServiceCollection AddMentionsModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        if (configuration.HasDatabaseConnectionString())
        {
            services.AddScoped<IMentionReplyJobRepository, EfMentionReplyJobRepository>();
            services.AddScoped<IMentionScanRepository, EfMentionScanRepository>();
        }

        services.AddScoped<IMentionScanService, MentionScanService>();
        services.AddScoped<IMentionReplyService, MentionReplyService>();
        services.AddScoped<IMentionAnswerService, AgentMentionAnswerService>();

        return services;
    }
}
