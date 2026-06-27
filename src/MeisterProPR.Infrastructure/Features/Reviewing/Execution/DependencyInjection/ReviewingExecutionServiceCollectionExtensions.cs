// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using MeisterProPR.ProRV.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
        services.AddScoped<IReviewStrategyDispatcher>(sp => new ReviewStrategyDispatcher(
            sp.GetRequiredService<IFileByFileReviewOrchestrator>(),
            sp.GetService<IReviewPipelineProfileProvider>()));
        services.AddSingleton<IReviewPipelineProfileProvider, ReviewPipelineProfileProvider>();
        services.AddScoped<IReviewPipeline<PerFileReviewContext>, ReviewPipelineRunner<PerFileReviewContext>>();
        services.AddScoped<IReviewPipelineStage<PerFileReviewContext>, FileByFileProRvPrefilterStage>();
        services.AddScoped<IReviewPipelineStage<PerFileReviewContext>, AgenticProRvPrefilterStage>();
        services.AddScoped<IReviewPipelineStage<PerFileReviewContext>, FileByFileContextPrefetchStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, FileByFileRiskMarkerStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, FileByFileImportanceRankingStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, FileByFileSelfReflectionRankingStage>();
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
        // Local verification = deterministic rules, plus (gated by AiReviewOptions.EnableEvidenceBackedVerification,
        // default off) an evidence-gathering verifier that escalates the claims deterministic rules can only
        // withhold for lack of bounded evidence. The composite is a no-op equal to the deterministic verifier
        // when the flag is off.
        services.AddSingleton<DeterministicLocalReviewVerifier>();
        services.AddSingleton<EvidenceBackedReviewVerifier>();
        services.AddSingleton<IReviewFindingVerifier>(sp => new CompositeReviewFindingVerifier(
            sp.GetRequiredService<DeterministicLocalReviewVerifier>(),
            sp.GetRequiredService<EvidenceBackedReviewVerifier>(),
            sp.GetRequiredService<IOptions<AiReviewOptions>>()));
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
