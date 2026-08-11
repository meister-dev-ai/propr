// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.CodeAnalysis;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Screening;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using MeisterDev.ProPR.ProRV.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Runner.Execution;

/// <summary>
///     The review pipeline, composed for a host with no database and no credentials, from the same
///     collaborators and in the same shape as the control plane composes it.
///     <para>
///         This mirrors the in-process composition in <c>ReviewingModuleServiceCollectionExtensions</c> and
///         <c>AddReviewingExecution</c>, substituting only at the edges: completions go through the relay,
///         thread memory through the proxy, trace and results into the spool. Every deterministic part is
///         the same code on both sides: the finding gate, invariant facts, verification, relevance
///         filtering, triage, the lens prefilter, the profile catalogue and structural analysis. A
///         collaborator left out here would not produce a smaller review, it would produce a different one.
///     </para>
///     <para>
///         What cannot be supplied is declared, not dropped: every constructor parameter of the composed
///         types appears in <see cref="Report" /> as supplied, equivalent, or absent, a test holds the
///         report complete against those constructors, and the executor records a protocol event per
///         absence. When a collaborator is added to the in-process composition, the test fails here until
///         this composition names it too.
///     </para>
/// </summary>
internal sealed class RunnerReviewPipeline : IDisposable
{
    /// <summary>The filter the control plane's composition root selects; one constant on both sides.</summary>
    private const string SelectedCommentRelevanceFilterId = "hybrid-v1";

    private readonly ReviewTriageClassifier _classifier;

    private RunnerReviewPipeline(
        FileByFileReviewOrchestrator orchestrator,
        ReviewTriageClassifier classifier,
        IReadOnlyList<RunnerCompositionEntry> report)
    {
        this.Orchestrator = orchestrator;
        this._classifier = classifier;
        this.Report = report;
    }

    /// <summary>The orchestrator a leased job runs on.</summary>
    public FileByFileReviewOrchestrator Orchestrator { get; }

    /// <summary>Every constructor parameter of the composed pipeline, and how this side answers it.</summary>
    public IReadOnlyList<RunnerCompositionEntry> Report { get; }

    /// <summary>
    ///     Composes the pipeline for one leased job. The classifier this creates holds a semaphore, so the
    ///     pipeline is disposed with the job.
    /// </summary>
    public static RunnerReviewPipeline Compose(
        IOptions<AiReviewOptions> reviewOptions,
        IProtocolRecorder recorder,
        IReviewFileResultStore results,
        IChatClient defaultClient,
        IAiRuntimeResolver aiRuntimeResolver,
        ILogicalModelResolver logicalModelResolver,
        IThreadMemoryService memoryService,
        IProRVPrefilter proRvPrefilter,
        IStructuralCodeAnalyzer structuralAnalyzer,
        ILicensingCapabilityService licensing,
        Func<bool> budgetExhaustedSignal,
        ILoggerFactory loggerFactory)
    {
        var options = reviewOptions.Value;
        var logger = loggerFactory.CreateLogger<FileByFileReviewOrchestrator>();

        // The same seven stages the control plane registers, in the same construction. The screener is
        // handed the relay resolver, which cannot serve embeddings. The stage then degrades to keep-all and
        // records comment_screening_degraded per file, so the divergence is recorded on the trace.
        var perFilePipeline = new ReviewPipelineRunner<PerFileReviewContext>(
        [
            new FileByFileContextPrefetchStage(options, recorder, structuralAnalyzer),
            new FileByFileRiskMarkerStage(),
            new FileByFileConfidenceFloorStage(options, recorder),
            new FileByFileSemanticScreeningStage(
                new EmbeddingSemanticCommentScreener(
                    reviewOptions,
                    aiRuntimeResolver,
                    loggerFactory.CreateLogger<EmbeddingSemanticCommentScreener>()),
                recorder),
            new FileByFileInfoCommentStripStage(recorder),
            new FileByFileImportanceRankingStage(options),
            new FileByFileSelfReflectionRankingStage(options, loggerFactory.CreateLogger<FileByFileSelfReflectionRankingStage>()),
        ]);

        var claimExtractor = new DeterministicReviewClaimExtractor();
        var verifier = new CompositeReviewFindingVerifier(
            new DeterministicLocalReviewVerifier(),
            new EvidenceBackedReviewVerifier());
        var invariantFactProviders = new IReviewInvariantFactProvider[]
        {
            new DomainReviewInvariantFactProvider(),
            new PersistenceReviewInvariantFactProvider(),
        };

        var relevanceRegistry = new CommentRelevanceFilterRegistry(
            [
                new PassThroughCommentRelevanceFilter(),
                new HeuristicCommentRelevanceFilter(),
                new HybridCommentRelevanceFilter(new AiCommentRelevanceAmbiguityEvaluator(loggerFactory.CreateLogger<AiCommentRelevanceAmbiguityEvaluator>())),
            ],
            new CommentRelevanceFilterSelection(SelectedCommentRelevanceFilterId));

        var classifier = new ReviewTriageClassifier(
            aiRuntimeResolver,
            loggerFactory.CreateLogger<ReviewTriageClassifier>(),
            // The usage recorder writes to the database; the relay meters every completion centrally, so a
            // second record from here would double-count the call.
            usageRecorder: null);

        var fileReviewer = new FileReviewer(
            new ToolAwareAiReviewCore(
                null,
                reviewOptions,
                loggerFactory.CreateLogger<ToolAwareAiReviewCore>(),
                // Null on purpose and not a divergence: the core constructs the default managed-session
                // transport factory itself when none is injected.
                managedSessionTransportFactory: null),
            recorder,
            results,
            options,
            logger,
            perFilePipeline,
            aiConnectionRepository: null,
            aiClientFactory: null,
            memoryService,
            aiRuntimeResolver,
            new CommentRelevanceFilterExecutor(relevanceRegistry, recorder),
            invariantFactProviders,
            new LocalReviewVerificationExecutor(claimExtractor, verifier, recorder),
            new ReviewPipelineProfileProvider(),
            proRvPrefilter,
            classifier,
            logicalModelResolver,
            structuralAnalyzer);

        var planner = new FileReviewDispatchPlanner(
            results,
            recorder,
            fileReviewer,
            options,
            logger,
            budgetScopeAccessor: null,
            licensing,
            budgetExhaustedSignal);

        var orchestrator = new FileByFileReviewOrchestrator(
            recorder,
            results,
            defaultClient,
            reviewOptions,
            logger,
            fileReviewer,
            planner,
            reviewSynthesisExecutor: null,
            candidateFindingFactory: null,
            qualityFilterExecutor: null,
            new PrLevelReviewVerificationExecutor(claimExtractor, new ReviewContextEvidenceCollector(), recorder, options),
            aiConnectionRepository: null,
            aiClientFactory: null,
            aiRuntimeResolver,
            new DeterministicReviewFindingGate(),
            invariantFactProviders,
            claimExtractor,
            new SummaryReconciliationService(),
            prWideCandidateGeneratorFactory: null,
            logicalModelResolver);

        return new RunnerReviewPipeline(orchestrator, classifier, BuildReport());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        this._classifier.Dispose();
    }

    /// <summary>
    ///     Names every constructor parameter of the composed types. The dispositions carry the meaning: an
    ///     absent collaborator is a decided and recorded divergence, and a parameter missing from this list
    ///     fails the drift test until it is given a disposition.
    /// </summary>
    private static List<RunnerCompositionEntry> BuildReport()
    {
        const RunnerCompositionDisposition supplied = RunnerCompositionDisposition.Supplied;
        const RunnerCompositionDisposition equivalent = RunnerCompositionDisposition.Equivalent;
        const RunnerCompositionDisposition absent = RunnerCompositionDisposition.Absent;

        return
        [
            new("aiCore", supplied, "the same core, calling through the relay"),
            new("protocolRecorder", supplied, "the spooling recorder"),
            new("jobRepository", supplied, "the spooling file-result store"),
            new("options", supplied, "bound by the shared binder from the same variables"),
            new("logger", supplied, "this host's logger"),
            new(
                "perFilePipeline", supplied,
                "the same seven stages; semantic screening degrades per file with a trace event because the relay serves no embeddings"),
            new("memoryService", supplied, "proxied to the control plane, the fourth proxied lookup"),
            new("aiRuntimeResolver", supplied, "the relay resolver; every purpose resolves to the manifest's model bindings"),
            new("commentRelevanceFilterExecutor", supplied, "the same three filters and the same selected id"),
            new("commentRelevanceFilterRegistry", supplied, "inside the executor above"),
            new("reviewFindingVerifier", supplied, "the composite of deterministic and evidence-backed, inside local verification"),
            new("reviewEvidenceCollector", supplied, "collects against the proxied tools, inside PR-level verification"),
            new("reviewInvariantFactProviders", supplied, "both providers; their facts are literals"),
            new("localReviewVerificationExecutor", supplied, "deterministic rules plus the evidence-backed verifier over the relay"),
            new("pipelineProfileProvider", supplied, "the static catalogue"),
            new("proRvPrefilter", supplied, "the embedded knowledge catalog"),
            new("complexityClassifier", supplied, "model-judged triage over the relay"),
            new("logicalModelResolver", supplied, "the relay resolver over the manifest's pass bindings"),
            new("structuralAnalyzer", supplied, "tree-sitter and Roslyn over the local worktrees"),
            new("chatClient", supplied, "the relayed default model"),
            new("fileReviewer", supplied, "composed above"),
            new("fileReviewDispatchPlanner", supplied, "composed above; two of its dependencies are absent, named below"),
            new("prLevelReviewVerificationExecutor", supplied, "claim extraction and evidence collection against the proxied tools"),
            new("deterministicReviewFindingGate", supplied, "the same deterministic rules"),
            new("reviewClaimExtractor", supplied, "compiled regexes"),
            new("summaryReconciliationService", supplied, "a pure function"),
            new(
                "aiConnectionRepository",
                equivalent,
                "connections resolve centrally; the runtime resolver answers first on both sides and the relay names models, never connections"),
            new(
                "aiClientFactory",
                equivalent,
                "clients are relay-built; the factory is the fallback behind the runtime resolver on both sides"),
            new("reviewSynthesisExecutor", equivalent, "self-built by the orchestrator from the collaborators supplied here, as in-process"),
            new("candidateFindingFactory", equivalent, "self-built by the orchestrator over the supplied claim extractor"),
            new("qualityFilterExecutor", equivalent, "self-built by the orchestrator"),
            new(
                "budgetScopeAccessor",
                equivalent,
                "the relay's budget signal gates new files instead; the figures stay where completions are priced"),
            new("budgetExhaustedSignal", supplied, "latched by the relay client on soft-cap or refusal"),
            new(
                "licensingCapabilityService",
                supplied,
                "answers from the manifest; the parallel-file capability was resolved at dispatch"),
            new(
                "prWideCandidateGeneratorFactory",
                absent,
                "pr_wide-scope pass entries do not run remotely; such entries are skipped with a log line"),
        ];
    }
}

/// <summary>How the runner composition answers one constructor parameter of the review pipeline.</summary>
/// <param name="Parameter">The constructor parameter's name.</param>
/// <param name="Disposition">Supplied outright, equivalent by construction, or absent by decision.</param>
/// <param name="Note">What answers it, or what its absence costs.</param>
internal sealed record RunnerCompositionEntry(
    string Parameter,
    RunnerCompositionDisposition Disposition,
    string Note);

/// <summary>The three honest answers a composition can give for a collaborator.</summary>
internal enum RunnerCompositionDisposition
{
    /// <summary>Constructed and passed; the same code runs on both sides.</summary>
    Supplied,

    /// <summary>Passed as null, but the path that answers instead is the same one in-process prefers.</summary>
    Equivalent,

    /// <summary>Deliberately not available on this side; recorded on every review's trace.</summary>
    Absent,
}
