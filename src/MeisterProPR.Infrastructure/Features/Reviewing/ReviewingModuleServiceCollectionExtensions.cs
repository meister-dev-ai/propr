// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.ThreadMemory.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.AzureDevOps;
using MeisterProPR.Infrastructure.AzureDevOps.Stub;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Intake.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.ThreadMemory.DependencyInjection;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ApplicationIAiReviewCore = MeisterProPR.Application.Interfaces.IAiReviewCore;

namespace MeisterProPR.Infrastructure.Features.Reviewing;

/// <summary>
///     Extension methods for registering the Reviewing module.
/// </summary>
public static class ReviewingModuleServiceCollectionExtensions
{
    /// <summary>
    ///     Registers Reviewing persistence, orchestration, and diagnostics services.
    /// </summary>
    public static IServiceCollection AddReviewingModule(this IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        services.AddReviewingIntake(configuration);

        if (configuration.IsDatabaseModeEnabled(environment))
        {
            services.AddScoped<IJobRepository, JobRepository>();
            services.AddSingleton<IProtocolRecorder, EfProtocolRecorder>();
            services.AddScoped<IThreadMemoryRepository, ThreadMemoryRepository>();
            services.AddScoped<IMemoryActivityLog, MemoryActivityLogRepository>();
        }

        services.AddReviewingExecution();
        services.AddReviewingDiagnostics();
        services.AddReviewingThreadMemory();

        services.AddSingleton<ApplicationIAiReviewCore>(sp => new ToolAwareAiReviewCore(
            null,
            sp.GetRequiredService<IOptions<AiReviewOptions>>(),
            sp.GetRequiredService<ILogger<ToolAwareAiReviewCore>>()));
        services.AddScoped<IFileByFileReviewOrchestrator>(sp => new FileByFileReviewOrchestrator(
            sp.GetRequiredService<ApplicationIAiReviewCore>(),
            sp.GetRequiredService<IProtocolRecorder>(),
            sp.GetRequiredService<IJobRepository>(),
            null,
            sp.GetRequiredService<IOptions<AiReviewOptions>>(),
            sp.GetRequiredService<ILogger<FileByFileReviewOrchestrator>>(),
            sp.GetService<IAiConnectionRepository>(),
            sp.GetService<IAiChatClientFactory>(),
            sp.GetService<IThreadMemoryService>()));
        services.AddSingleton<IAiCommentResolutionCore, AgentAiCommentResolutionCore>();
        services.AddScoped<IThreadMemoryEmbedder, ThreadMemoryEmbedder>();
        services.AddScoped<IThreadMemoryService, ThreadMemoryService>();
        services.AddTransient<ReviewOrchestrationService>();

        if (configuration.GetValue<bool>("ADO_STUB_PR"))
        {
            services.AddScoped<IReviewContextToolsFactory, StubReviewContextToolsFactory>();
            services.AddScoped<IRepositoryInstructionFetcher, NullRepositoryInstructionFetcher>();
            services.AddScoped<IRepositoryExclusionFetcher, NullRepositoryExclusionFetcher>();
        }
        else
        {
            services.AddScoped<IReviewContextToolsFactory>(sp =>
                new AdoReviewContextToolsFactory(
                    sp.GetRequiredService<VssConnectionFactory>(),
                    sp.GetRequiredService<IClientAdoCredentialRepository>(),
                    sp.GetRequiredService<IProCursorGateway>(),
                    sp.GetRequiredService<IOptions<AiReviewOptions>>(),
                    sp.GetRequiredService<ILoggerFactory>()));
            services.AddScoped<IRepositoryInstructionFetcher, AdoRepositoryInstructionFetcher>();
            services.AddScoped<IRepositoryExclusionFetcher, AdoRepositoryExclusionFetcher>();
        }

        if (!string.IsNullOrWhiteSpace(configuration["AI_EVALUATOR_ENDPOINT"]) &&
            !string.IsNullOrWhiteSpace(configuration["AI_EVALUATOR_DEPLOYMENT"]))
        {
            services.AddSingleton<IRepositoryInstructionEvaluator, AiRepositoryInstructionEvaluator>();
        }
        else
        {
            services.AddSingleton<IRepositoryInstructionEvaluator, PassThroughRepositoryInstructionEvaluator>();
        }

        return services;
    }
}
