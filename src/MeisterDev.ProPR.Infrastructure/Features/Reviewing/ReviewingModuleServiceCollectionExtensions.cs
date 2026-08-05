// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.ThreadMemory.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Threads.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Threads.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.ThreadMemory;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.CodeAnalysis;
using MeisterDev.ProPR.CodeAnalysis.Roslyn.DependencyInjection;
using MeisterDev.ProPR.CodeAnalysis.TreeSitter.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Budgeting;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Intake.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.PostedFindings;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.ThreadMemory.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Threads.Persistence;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.ProRV.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ApplicationIAiReviewCore = MeisterDev.ProPR.Application.Interfaces.IAiReviewCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing;

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

        // Bound before the database gate so the back-fill worker resolves its budget even on a host where the
        // rest of this module stays inert.
        services.AddOptions<ThreadMemoryKeywordOptions>().Configure(memoryOptions =>
        {
            // The back-fill budget used to be CODE_INSIGHTS_MEMORY_KEYWORD_BACKFILL_MAX, from when keyword
            // extraction lived in that feature. The old name is still read, because an installation that had
            // set it would otherwise stop back-filling on upgrade with nothing said: an unset key and a
            // deliberate zero are indistinguishable.
            memoryOptions.BackfillMax = configuration.GetValue(
                "AI_MEMORY_KEYWORD_BACKFILL_MAX",
                configuration.GetValue(
                    "CODE_INSIGHTS_MEMORY_KEYWORD_BACKFILL_MAX",
                    memoryOptions.BackfillMax));
            memoryOptions.SweepIntervalSeconds = configuration.GetValue(
                "AI_MEMORY_KEYWORD_SWEEP_INTERVAL_SECONDS",
                memoryOptions.SweepIntervalSeconds);
        });

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
            services.AddScoped<IThreadPassJobRepository, EfThreadPassJobRepository>();
            services.AddScoped<IReviewSpendAccumulator, ReviewSpendAccumulator>();

            // Search keywords on resolution memories, extracted from text the memory already carries. The
            // extractor runs as a memory is stored; the sweeper exists only for memories written before it did,
            // and its budget is zero unless an installation asks for a back-fill.
            services.AddScoped<IMemoryKeywordExtractor, AiMemoryKeywordExtractor>();
            services.AddScoped<IThreadMemoryKeywordSweeper, ThreadMemoryKeywordSweeper>();
            services.AddScoped<IBudgetCapsProvider, BudgetCapsProvider>();
            services.AddScoped<IClientBudgetConsumptionService, ClientBudgetConsumptionService>();
            services.AddScoped<ITenantBudgetOverviewService, TenantBudgetOverviewService>();
            services.AddScoped<ITenantBudgetSpendService, TenantBudgetSpendService>();
            services.AddScoped<IBudgetSpendResetRepository, BudgetSpendResetRepository>();
            services.AddScoped<IClientBudgetResetService, ClientBudgetResetService>();
            services.AddScoped<IBudgetEventRepository, BudgetEventRepository>();
            services.AddScoped<IBudgetEventPublisher, BudgetEventPublisher>();
            services.AddSingleton<IModelPricingResolver, EfModelPricingResolver>();
            services.AddSingleton<IProtocolRecorder, EfProtocolRecorder>();
            services.AddScoped<IThreadMemoryRepository, ThreadMemoryRepository>();
            services.AddScoped<IPostedFindingRepository, PostedFindingRepository>();
            services.AddScoped<IMemoryActivityLog, MemoryActivityLogRepository>();
        }
        else
        {
            services.AddOfflineReviewing(configuration);
        }

        services.AddReviewingExecution(selectedCommentRelevanceFilterId);

        // The conversation runs on its own cadence, beside the file review rather than inside it.
        services.AddScoped<IThreadPassService, ThreadPassService>();

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
            sp.GetService<IReviewComplexityClassifier>(),
            sp.GetService<ILogicalModelResolver>(),
            // The same structural analyzer the context stages use, so a finding can name the definition it sits in.
            sp.GetService<IStructuralCodeAnalyzer>()));
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
            // The structural analyzer reaches the review through the FileReviewer above: this orchestrator never
            // resolves a definition itself.
            () => sp.GetService<IPrWideCandidateGenerator>(),
            sp.GetService<ILogicalModelResolver>()));
        if (!hasDatabase)
        {
            services.AddScoped<IReviewWorkflowRunner, ReviewWorkflowRunner>();
        }

        services.AddScoped<IPrWideAgenticReviewOrchestrator, PrWideAgenticReviewOrchestrator>();

        // The same PR-wide orchestrator instance also exposes the generate-only entry point the file-by-file
        // orchestrator uses to run a pr_wide-scope pass at the job level.
        services.AddScoped<IPrWideCandidateGenerator>(sp => (PrWideAgenticReviewOrchestrator)sp.GetRequiredService<IPrWideAgenticReviewOrchestrator>());
        services.AddSingleton<IAiCommentResolutionCore, AgentAiCommentResolutionCore>();

        services.TryAddSingleton<IMemoryReconsiderationPromptBuilder, MemoryReconsiderationPromptBuilder>();

        if (hasDatabase)
        {
            services.AddScoped<IThreadMemoryEmbedder, ThreadMemoryEmbedder>();
            services.AddScoped<IThreadMemoryService, ThreadMemoryService>();
            services.AddScoped<IPostedFindingIndex, PostedFindingIndex>();
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
