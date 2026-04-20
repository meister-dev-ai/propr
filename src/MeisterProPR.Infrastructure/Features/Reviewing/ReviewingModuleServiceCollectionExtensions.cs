// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Intake.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.ThreadMemory.DependencyInjection;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public static IServiceCollection AddReviewingModule(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        if (configuration.HasDatabaseConnectionString())
        {
            services.TryAddScoped<IScmProviderRegistry, ScmProviderRegistry>();
            services.AddAzureDevOpsProviderAdapters();
            services.AddGitHubProviderAdapters();
            services.AddGitLabProviderAdapters();
            services.AddForgejoProviderAdapters();
        }

        services.AddReviewingIntake(configuration);

        if (configuration.HasDatabaseConnectionString())
        {
            services.AddScoped<IJobRepository, JobRepository>();
            services.AddSingleton<IProtocolRecorder, EfProtocolRecorder>();
            services.AddScoped<IThreadMemoryRepository, ThreadMemoryRepository>();
            services.AddScoped<IMemoryActivityLog, MemoryActivityLogRepository>();
        }

        services.AddReviewingExecution();
        services.AddReviewingDiagnostics();
        services.AddReviewingThreadMemory();
        services.TryAddScoped<IRepositoryInstructionFetcher, ProviderRepositoryInstructionFetcher>();
        services.TryAddScoped<IRepositoryExclusionFetcher, ProviderRepositoryExclusionFetcher>();

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
        services.TryAddScoped<IReviewerThreadStatusFetcher, ProviderReviewerThreadStatusFetcher>();
        services.AddTransient<ReviewOrchestrationService>();

        services.AddAzureDevOpsReviewingServices(configuration);

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
