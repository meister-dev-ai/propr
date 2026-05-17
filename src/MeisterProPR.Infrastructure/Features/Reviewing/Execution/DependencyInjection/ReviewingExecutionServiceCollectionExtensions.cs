// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using MeisterProPR.ProRV.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;

/// <summary>
///     Registers Reviewing execution boundaries.
/// </summary>
public static class ReviewingExecutionServiceCollectionExtensions
{
    /// <summary>
    ///     Registers Reviewing execution adapters.
    /// </summary>
    public static IServiceCollection AddReviewingExecution(
        this IServiceCollection services,
        string? selectedCommentRelevanceFilterId = null)
    {
        services.AddScoped<IReviewJobExecutionStore>(sp =>
            new ReviewJobExecutionStoreAdapter(sp.GetRequiredService<IJobRepository>()));
        services.AddScoped<IReviewStrategyDispatcher, ReviewStrategyDispatcher>();
        services.AddSingleton<IReviewPipelineProfileProvider, ReviewPipelineProfileProvider>();
        services.AddScoped<IReviewPipeline<PerFileReviewContext>, ReviewPipelineRunner<PerFileReviewContext>>();
        services.AddScoped<IReviewPipelineStage<PerFileReviewContext>, FileByFileProRvPrefilterStage>();
        services.AddScoped<IReviewPipelineStage<PerFileReviewContext>, AgenticProRvPrefilterStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, FileByFileConfidenceFloorStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, FileByFileSpeculativeCommentFilterStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, FileByFileInfoCommentStripStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, FileByFileVagueSuggestionFilterStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, AgenticConfidenceFloorStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, AgenticSpeculativeCommentFilterStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, AgenticInfoCommentStripStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, AgenticVagueSuggestionFilterStage>();
        services.AddTransient<IReviewJobProcessor>(sp => sp.GetRequiredService<ReviewOrchestrationService>());
        services.AddCommentRelevanceFiltering(selectedCommentRelevanceFilterId);
        services.AddSingleton<IDeterministicReviewFindingGate, DeterministicReviewFindingGate>();
        services.AddSingleton<IReviewInvariantFactProvider, DomainReviewInvariantFactProvider>();
        services.AddSingleton<IReviewInvariantFactProvider, PersistenceReviewInvariantFactProvider>();
        services.AddSingleton<IReviewClaimExtractor, DeterministicReviewClaimExtractor>();
        services.AddSingleton<IReviewFindingVerifier, DeterministicLocalReviewVerifier>();
        services.AddProRV();
        services.AddSingleton<LocalReviewVerificationExecutor>();
        services.AddSingleton<IReviewEvidenceCollector, ReviewContextEvidenceCollector>();
        services.AddSingleton<PrLevelReviewVerificationExecutor>();
        services.AddSingleton<CandidateFindingFactory>();
        services.AddSingleton<AgenticCandidateFindingFactory>();
        services.AddSingleton<QualityFilterExecutor>();
        services.AddScoped<FileReviewDispatchPlanner>();
        services.AddScoped<AgenticFileReviewDispatchPlanner>();
        services.AddScoped<ReviewSynthesisExecutor>();
        services.AddScoped<AgenticReviewSynthesisExecutor>();
        services.AddSingleton<ISummaryReconciliationService, SummaryReconciliationService>();

        return services;
    }
}
