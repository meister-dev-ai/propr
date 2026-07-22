// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.CodeAnalysis;
using MeisterProPR.CodeAnalysis.Roslyn.DependencyInjection;
using MeisterProPR.CodeAnalysis.TreeSitter.DependencyInjection;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Budgeting;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using MeisterProPR.Infrastructure.Features.Reviewing.Intake.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.Offline.DependencyInjection;
using MeisterProPR.Infrastructure.Features.Reviewing.ThreadMemory.DependencyInjection;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.ProRV.Abstractions;
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
        IHostEnvironment? environment = null,
        string? selectedCommentRelevanceFilterId = null)
    {
        var hasDatabase = configuration.HasDatabaseConnectionString();

        if (hasDatabase)
        {
            services.TryAddScoped<IScmProviderRegistry, ScmProviderRegistry>();
            services.AddAzureDevOpsProviderAdapters();
            services.AddGitHubProviderAdapters();
            services.AddGitLabProviderAdapters();
            services.AddForgejoProviderAdapters();
        }

        services.AddReviewingIntake();

        // The budget scope accessor is a pure ambient holder (no database), so it is always available; the
        // enforcing model-client decorators read it on each call and are inert when no scope is active.
        services.TryAddSingleton<IBudgetScopeAccessor, BudgetScopeAccessor>();

        // Ambient wall-clock used by the budget consumption report to resolve the current monthly period.
        services.TryAddSingleton(TimeProvider.System);

        if (hasDatabase)
        {
            services.AddScoped<IJobRepository, JobRepository>();
            services.AddScoped<IReviewSpendAccumulator, ReviewSpendAccumulator>();
            services.AddScoped<IBudgetCapsProvider, BudgetCapsProvider>();
            services.AddScoped<IClientBudgetConsumptionService, ClientBudgetConsumptionService>();
            services.AddScoped<IBudgetEventRepository, BudgetEventRepository>();
            services.AddScoped<IBudgetEventPublisher, BudgetEventPublisher>();
            services.AddSingleton<IModelPricingResolver, EfModelPricingResolver>();
            services.AddSingleton<IProtocolRecorder, EfProtocolRecorder>();
            services.AddScoped<IThreadMemoryRepository, ThreadMemoryRepository>();
            services.AddScoped<IMemoryActivityLog, MemoryActivityLogRepository>();
        }
        else
        {
            services.AddOfflineReviewing(configuration);
        }

        services.AddReviewingExecution(selectedCommentRelevanceFilterId);

        // Unified code-analysis abstraction: register both backends as concrete
        // singletons, then expose the composite router as the single IStructuralCodeAnalyzer every
        // consumer (prefetch, tools, related_symbol) depends on. C# routes to Roslyn-syntax; the
        // seven Tree-sitter languages route to the Tree-sitter backend.
        services.AddCodeAnalysisTreeSitter();
        services.AddCodeAnalysisRoslyn();
        services.TryAddSingleton<IStructuralCodeAnalyzer>(sp => new CompositeStructuralCodeAnalyzer(
            new[]
            {
                sp.GetRequiredKeyedService<IStructuralCodeAnalyzer>(CodeAnalysisServiceCollectionExtensions.BackendKey),
                sp.GetRequiredKeyedService<IStructuralCodeAnalyzer>(CodeAnalysisRoslynServiceCollectionExtensions.BackendKey),
            }));
        services.AddReviewingDiagnostics();
        services.AddReviewingThreadMemory();
        services.TryAddScoped<IRepositoryInstructionFetcher, ProviderRepositoryInstructionFetcher>();
        services.TryAddScoped<IRepositoryExclusionFetcher, ProviderRepositoryExclusionFetcher>();
        services.TryAddSingleton(sp =>
        {
            var contentRootPath = environment?.ContentRootPath ?? AppContext.BaseDirectory;
            return new PromptTemplateFileProvider(contentRootPath);
        });
        services.TryAddSingleton(sp => new PromptTemplatePartialRegistry(sp.GetRequiredService<PromptTemplateFileProvider>()));
        services.TryAddSingleton(_ => new HandlebarsPromptRenderer());

        services.AddSingleton<ApplicationIAiReviewCore>(sp => new ToolAwareAiReviewCore(
            null,
            sp.GetRequiredService<IOptions<AiReviewOptions>>(),
            sp.GetRequiredService<ILogger<ToolAwareAiReviewCore>>(),
            sp.GetService<IManagedReviewSessionTransportFactory>()));
        services.TryAddSingleton<IManagedReviewSessionTransportFactory, ManagedReviewSessionTransportFactory>();
        services.AddScoped<IReviewComplexityClassifier, ReviewTriageClassifier>();
        services.AddScoped<FileReviewer>(sp => new FileReviewer(
            sp.GetRequiredService<ApplicationIAiReviewCore>(),
            sp.GetRequiredService<IProtocolRecorder>(),
            sp.GetRequiredService<IJobRepository>(),
            sp.GetRequiredService<IOptions<AiReviewOptions>>().Value,
            sp.GetRequiredService<ILogger<FileByFileReviewOrchestrator>>(),
            sp.GetService<IReviewPipeline<PerFileReviewContext>>(),
            sp.GetService<IAiConnectionRepository>(),
            sp.GetService<IAiChatClientFactory>(),
            sp.GetService<IThreadMemoryService>(),
            sp.GetService<IAiRuntimeResolver>(),
            sp.GetService<CommentRelevanceFilterExecutor>(),
            sp.GetServices<IReviewInvariantFactProvider>(),
            sp.GetService<LocalReviewVerificationExecutor>(),
            sp.GetService<IReviewPipelineProfileProvider>(),
            sp.GetService<IProRVPrefilter>(),
            sp.GetService<IReviewComplexityClassifier>()));
        services.AddScoped<IFileByFileReviewOrchestrator>(sp => new FileByFileReviewOrchestrator(
            sp.GetRequiredService<IProtocolRecorder>(),
            sp.GetRequiredService<IJobRepository>(),
            null,
            sp.GetRequiredService<IOptions<AiReviewOptions>>(),
            sp.GetRequiredService<ILogger<FileByFileReviewOrchestrator>>(),
            sp.GetRequiredService<FileReviewer>(),
            sp.GetService<FileReviewDispatchPlanner>(),
            sp.GetService<ReviewSynthesisExecutor>(),
            sp.GetService<CandidateFindingFactory>(),
            sp.GetService<QualityFilterExecutor>(),
            sp.GetService<PrLevelReviewVerificationExecutor>(),
            sp.GetService<IAiConnectionRepository>(),
            sp.GetService<IAiChatClientFactory>(),
            sp.GetService<IAiRuntimeResolver>(),
            sp.GetService<IDeterministicReviewFindingGate>(),
            sp.GetServices<IReviewInvariantFactProvider>(),
            sp.GetService<IReviewClaimExtractor>(),
            sp.GetService<ISummaryReconciliationService>(),
            // Lazy: resolved only when a pr_wide-scope pass entry runs, after this orchestrator is constructed, so
            // the PR-wide generator's dependency back on the file-by-file orchestrator does not form a DI cycle.
            () => sp.GetService<IPrWideCandidateGenerator>()));
        if (!hasDatabase)
        {
            services.AddScoped<IReviewWorkflowRunner, ReviewWorkflowRunner>();
        }

        services.AddScoped<IPrWideAgenticReviewOrchestrator, PrWideAgenticReviewOrchestrator>();

        // The same PR-wide orchestrator instance also exposes the generate-only entry point the file-by-file
        // orchestrator uses to run a pr_wide-scope pass at the job level.
        services.AddScoped<IPrWideCandidateGenerator>(sp => (PrWideAgenticReviewOrchestrator)sp.GetRequiredService<IPrWideAgenticReviewOrchestrator>());
        services.AddSingleton<IAiCommentResolutionCore, AgentAiCommentResolutionCore>();

        if (hasDatabase)
        {
            services.AddScoped<IThreadMemoryEmbedder, ThreadMemoryEmbedder>();
            services.AddScoped<IThreadMemoryService, ThreadMemoryService>();
        }

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
