// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Deduplication;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Screening;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using MeisterDev.ProPR.ProRV.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.DependencyInjection;

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
        // The pipeline asks for the narrow store; on the control plane the repository is it. A runner
        // registers a buffering implementation of the same four methods instead.
        services.AddScoped<IReviewFileResultStore>(sp => sp.GetRequiredService<IJobRepository>());
        // TryAdd so the offline harness, which has no database, can register its own in-memory claiming.
        services.TryAddScoped<IReviewJobLeaseStore>(sp => new ReviewJobLeaseStore(
            sp.GetRequiredService<MeisterProPRDbContext>(),
            sp.GetRequiredService<IJobRepository>(),
            sp.GetRequiredService<IOptions<ReviewLeaseOptions>>(),
            sp.GetRequiredService<ILogger<ReviewJobLeaseStore>>()));
        // Singleton so the background worker and the control-plane stop endpoint share the same
        // per-job cancellation sources for prompt in-flight interruption on this instance.
        services.AddSingleton<IReviewJobCancellationRegistry, ReviewJobCancellationRegistry>();
        services.AddSingleton<IReviewPipelineProfileProvider, ReviewPipelineProfileProvider>();
        // Resolves the job manifest once at dispatch. Reads only application ports, so it lives in the
        // application layer and needs nothing from infrastructure beyond being registered.
        services.AddScoped<IRunnerJobManifestResolver, RunnerJobManifestResolver>();
        // One implementation of "what may this review adopt" for both execution paths: the in-process
        // orchestration builds the same type from its own dependencies, and the dispatch preparer resolves
        // this one. Two implementations would let a remote review become a different review.
        services.AddScoped(sp => new ReviewJobReuse(
            sp.GetRequiredService<IReviewJobExecutionStore>(),
            sp.GetRequiredService<IReviewPrScanWatermarkStore>(),
            sp.GetRequiredService<ILogger<ReviewJobReuse>>()));
        // Every proxied call an executor makes is authorized against the lease it presents, before anything
        // else looks at the request.
        services.AddScoped<IRunnerCallAuthorizer, RunnerCallAuthorizer>();
        // Singleton: the tools it holds are live provider clients scoped to a lease, not to a request.
        services.AddSingleton<IRunnerJobToolsRegistry, RunnerJobToolsRegistry>();
        services.AddScoped<IRunnerToolProxy, RunnerToolProxy>();
        // Thread memory is the fourth proxied lookup. Its optional service parameter resolves to null on
        // an installation without a memory store, which the proxy answers as not-offered.
        services.AddScoped<IRunnerMemoryProxy, RunnerMemoryProxy>();
        // Singleton for the same reason as the tools registry: a job's budget belongs to the lease, not to
        // whichever request thread happens to serve a runner's completion.
        services.AddSingleton<IRunnerJobBudgetRegistry, RunnerJobBudgetRegistry>();
        services.AddScoped<IRunnerIngestLedger, RunnerIngestLedger>();
        services.AddScoped<IRunnerIngestWriter, RunnerIngestWriter>();
        services.AddScoped<IRunnerIngestService, RunnerIngestService>();
        services.AddScoped<IRunnerRegistry, RunnerRegistry>();
        // What the fleet is, and what it is doing, are read separately: one lives on the runners and the
        // other on the jobs they hold.
        services.AddScoped<IRunnerWorkloadReader, RunnerWorkloadReader>();
        // Offer selection and the expensive dispatch preparation are separate registrations so the rules
        // that decide who may see which job can be exercised without a git remote.
        services.AddScoped<IRunnerLeaseOfferStore, RunnerLeaseOfferStore>();
        services.AddScoped<IRunnerJobDispatchPreparer, RunnerJobDispatchPreparer>();
        services.AddScoped<IRunnerSlotEntitlement, RunnerSlotEntitlement>();
        // One predicate for "is this installation running reviews on runners", asked by the worker before
        // it claims and by the stall check that explains an idle queue. TryAdd so the offline harness,
        // which has no database for this to read, keeps the empty-fleet monitor it registered first.
        services.TryAddScoped<IRunnerFleetMonitor, RunnerFleetMonitor>();
        services.AddScoped<IRunnerLeaseOfferService, RunnerLeaseOfferService>();
        // Singleton like the other per-lease registries: a mirror is a path on this host's disk, so
        // the replica that granted the lease is the one that can serve it.
        services.AddSingleton<IRunnerWorkspaceRegistry, RunnerWorkspaceRegistry>();
        services.AddSingleton<IRunnerWorkspaceSizeProbe, DirectorySizeProbe>();
        services.AddSingleton<IGitUploadPackTransport, GitUploadPackTransport>();
        services.AddScoped<IRunnerWorkspaceServer, RunnerWorkspaceServer>();
        services.AddScoped<IRunnerRegistrationService, RunnerRegistrationService>();
        services.AddScoped<IRunnerRelayModelResolver, RunnerRelayModelResolver>();
        // Singleton: the idempotency it holds spans the calls of one lease, not one request.
        services.AddSingleton<IRunnerRelayUsageRecorder, RunnerRelayUsageRecorder>();
        // Singleton for the same reason: a retry after a network failure arrives on a different request,
        // and a replay cache scoped to the first one would charge the retry as a second completion.
        services.AddSingleton<RunnerRelayReplayCache>();
        services.AddScoped<IRunnerAiRelay, RunnerAiRelay>();
        services.AddScoped<IReviewPipeline<PerFileReviewContext>, ReviewPipelineRunner<PerFileReviewContext>>();
        services.AddScoped<IReviewPipelineStage<PerFileReviewContext>, FileByFileContextPrefetchStage>();
        services.AddScoped<IReviewPipelineStage<PerFileReviewContext>, FileByFileSemanticScreeningStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, FileByFileRiskMarkerStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, FileByFileImportanceRankingStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, FileByFileSelfReflectionRankingStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, FileByFileConfidenceFloorStage>();
        services.AddSingleton<IReviewPipelineStage<PerFileReviewContext>, FileByFileInfoCommentStripStage>();
        services.AddTransient<IReviewJobProcessor>(sp => sp.GetRequiredService<ReviewOrchestrationService>());
        // The same instance behind both: a runner's findings and an in-process review must end on one
        // publication, and two registrations would be two code paths that could drift.
        services.AddTransient<IReviewResultPublisher>(sp => sp.GetRequiredService<ReviewOrchestrationService>());
        // The ledger is a singleton because the two things intake correlates never arrive on one request:
        // the chunks of one submission, and a resend against what already published.
        services.AddSingleton<RunnerSubmissionLedger>();
        services.AddScoped<IRunnerFindingsIntake, RunnerFindingsIntake>();

        // What a resuming executor reads back. Registered beside the findings intake because the two are
        // counterparts: one sends results out, the other returns them on a reclaim.
        services.AddScoped<IRunnerPriorResultsReader, RunnerPriorResultsReader>();
        services.AddCommentRelevanceFiltering(selectedCommentRelevanceFilterId);
        services.AddSingleton<IDeterministicReviewFindingGate, DeterministicReviewFindingGate>();
        // Post-gate finalization checks compose on top of the deterministic gate without altering it. The
        // reread-before-ERROR floor is the first check; further checks join by being registered here.
        services.AddSingleton<IFindingFinalizationCheck, RereadFinalizationCheck>();
        services.AddScoped<IReviewFindingFinalizationPipeline, ReviewFindingFinalizationPipeline>();
        services.AddSingleton<IReviewInvariantFactProvider, DomainReviewInvariantFactProvider>();
        services.AddSingleton<IReviewInvariantFactProvider, PersistenceReviewInvariantFactProvider>();
        services.AddSingleton<IReviewClaimExtractor, DeterministicReviewClaimExtractor>();
        // Local verification = deterministic rules, plus (gated per-client via the review context's
        // EvidenceVerificationEnabled flag, default off) an evidence-gathering verifier that escalates the
        // claims deterministic rules can only withhold for lack of bounded evidence. The composite is a no-op
        // equal to the deterministic verifier when the per-client flag is off.
        services.AddSingleton<DeterministicLocalReviewVerifier>();
        services.AddSingleton<EvidenceBackedReviewVerifier>();
        services.AddSingleton<IReviewFindingVerifier>(sp => new CompositeReviewFindingVerifier(
            sp.GetRequiredService<DeterministicLocalReviewVerifier>(),
            sp.GetRequiredService<EvidenceBackedReviewVerifier>()));
        services.AddProRV();
        services.AddSingleton<LocalReviewVerificationExecutor>();
        services.AddSingleton<IReviewEvidenceCollector, ReviewContextEvidenceCollector>();
        services.AddSingleton<PrLevelReviewVerificationExecutor>();
        services.AddSingleton<CandidateFindingFactory>();
        services.AddSingleton<QualityFilterExecutor>();
        services.AddScoped<FileReviewDispatchPlanner>();
        // Semantic finding dedup = same file + overlapping anchor + AI-judged same defect class, gated per-client
        // via the review context's EnableMultiPassUnion flag (default off). The judge is degraded-safe: when no
        // verification model is bound it returns keep-both, so the deduplicator only ever collapses confirmed
        // duplicates and never merges distinct bugs.
        services.AddScoped<IFindingMergeJudge, AiFindingMergeJudge>();
        services.AddScoped<IFindingDeduplicator, SemanticFindingDeduplicator>();
        services.AddScoped<ISemanticCommentScreener, EmbeddingSemanticCommentScreener>();
        services.AddScoped<ReviewSynthesisExecutor>();
        services.AddSingleton<ISummaryReconciliationService, SummaryReconciliationService>();

        return services;
    }
}
