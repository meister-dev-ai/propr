// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Interfaces;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MeisterProPR.Infrastructure.Features.Mentions;

/// <summary>
///     Extension methods for registering the Mentions module.
/// </summary>
public static class MentionsModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers mention scan, reply, and AI answer services.
    /// </summary>
    public static IServiceCollection AddMentionsModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        if (configuration.IsDatabaseModeEnabled(environment))
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
