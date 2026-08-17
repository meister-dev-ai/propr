// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Mentions.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Interfaces;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Mentions.Persistence;
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

            // What the scan reads to decide which repositories this installation answers on. Without a
            // database there is nothing to configure and nothing to scan.
            services.AddScoped<IMentionConfigurationRepository, MentionConfigurationRepository>();

            // Registered beside the repository it guards, because both read the client connections a
            // configuration is judged against.
            services.AddScoped<IMentionConfigurationScopeValidator, MentionConfigurationScopeValidator>();

            // Rebuilds provenance a crash left unwritten. Registered under the same gate as the job repository
            // it reads, and as the provenance store the review-archive module registers, so it exists exactly
            // when both of its inputs do.
            services.AddScoped<IMentionReplyProvenanceReconciler, MentionReplyProvenanceReconciler>();
        }

        services.AddScoped<IMentionScanService, MentionScanService>();
        services.AddScoped<IMentionReplyService, MentionReplyService>();
        services.AddScoped<IMentionAnswerService, AgentMentionAnswerService>();

        return services;
    }
}
