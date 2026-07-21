// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Deduplication;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Screening;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using MeisterProPR.ProRV.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

internal sealed partial class FileByFileReviewOrchestrator(
    IProtocolRecorder protocolRecorder,
    IJobRepository jobRepository,
    IChatClient? chatClient,
    IOptions<AiReviewOptions> options,
    ILogger<FileByFileReviewOrchestrator> logger,
    FileReviewer fileReviewer,
    FileReviewDispatchPlanner? fileReviewDispatchPlanner = null,
    ReviewSynthesisExecutor? reviewSynthesisExecutor = null,
    CandidateFindingFactory? candidateFindingFactory = null,
    QualityFilterExecutor? qualityFilterExecutor = null,
    PrLevelReviewVerificationExecutor? prLevelReviewVerificationExecutor = null,
    IAiConnectionRepository? aiConnectionRepository = null,
    IAiChatClientFactory? aiClientFactory = null,
    IAiRuntimeResolver? aiRuntimeResolver = null,
    IDeterministicReviewFindingGate? deterministicReviewFindingGate = null,
    IEnumerable<IReviewInvariantFactProvider>? reviewInvariantFactProviders = null,
    IReviewClaimExtractor? reviewClaimExtractor = null,
    ISummaryReconciliationService? summaryReconciliationService = null,
    Func<IPrWideCandidateGenerator?>? prWideCandidateGeneratorFactory = null) : IFileByFileReviewOrchestrator
{
    private readonly AiReviewOptions _opts = options.Value;

    public FileByFileReviewOrchestrator(
        IAiReviewCore aiCore,
        IProtocolRecorder protocolRecorder,
        IJobRepository jobRepository,
        IChatClient? chatClient,
        IOptions<AiReviewOptions> options,
        ILogger<FileByFileReviewOrchestrator> logger,
        IAiConnectionRepository? aiConnectionRepository = null,
        IAiChatClientFactory? aiClientFactory = null,
        IThreadMemoryService? memoryService = null,
        IAiRuntimeResolver? aiRuntimeResolver = null,
        CommentRelevanceFilterRegistry? commentRelevanceFilterRegistry = null,
        IDeterministicReviewFindingGate? deterministicReviewFindingGate = null,
        IEnumerable<IReviewInvariantFactProvider>? reviewInvariantFactProviders = null,
        IReviewClaimExtractor? reviewClaimExtractor = null,
        IReviewFindingVerifier? reviewFindingVerifier = null,
        IReviewEvidenceCollector? reviewEvidenceCollector = null,
        ISummaryReconciliationService? summaryReconciliationService = null,
        IReviewPipeline<PerFileReviewContext>? perFilePipeline = null,
        IReviewPipelineProfileProvider? pipelineProfileProvider = null,
        IProRVPrefilter? proRvPrefilter = null,
        Func<IPrWideCandidateGenerator?>? prWideCandidateGeneratorFactory = null)
        : this(
            protocolRecorder,
            jobRepository,
            chatClient,
            options,
            logger,
            new FileReviewer(
                aiCore,
                protocolRecorder,
                jobRepository,
                options.Value,
                logger,
                perFilePipeline ?? CreateDefaultPerFilePipeline(
                    options.Value,
                    protocolRecorder,
                    proRvPrefilter,
                    aiConnectionRepository,
                    aiClientFactory,
                    aiRuntimeResolver),
                aiConnectionRepository,
                aiClientFactory,
                memoryService,
                aiRuntimeResolver,
                new CommentRelevanceFilterExecutor(commentRelevanceFilterRegistry, protocolRecorder),
                reviewInvariantFactProviders,
                new LocalReviewVerificationExecutor(reviewClaimExtractor, reviewFindingVerifier, protocolRecorder),
                pipelineProfileProvider,
                proRvPrefilter),
            null,
            null,
            null,
            null,
            new PrLevelReviewVerificationExecutor(reviewClaimExtractor, reviewEvidenceCollector, protocolRecorder, options.Value),
            aiConnectionRepository,
            aiClientFactory,
            aiRuntimeResolver,
            deterministicReviewFindingGate,
            reviewInvariantFactProviders,
            reviewClaimExtractor,
            summaryReconciliationService,
            prWideCandidateGeneratorFactory)
    {
    }

    public async Task<ReviewResult> ReviewAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        CancellationToken ct,
        IChatClient? overrideClient = null)
    {
        var effectiveClient = overrideClient ?? chatClient
            ?? throw new InvalidOperationException("No chat client available for file review orchestration.");
        await this.RecordReviewProfileSelectedEventAsync(job, baseContext, ct);
        var dispatchResult = await this.GetDispatchPlanner().ExecuteAsync(job, pr, baseContext, effectiveClient, ct);
        var budgetSoftCap = dispatchResult.BudgetSoftCap;

        // Job-level PR-wide-scope pass entries run once over the whole change set here — after the per-file fan-out
        // and before synthesis — and their candidates are threaded into synthesis so they flow through the shared
        // verify -> gate -> publish path alongside the synthesized cross-cutting findings. When the per-increment
        // soft cap has tripped, this is further scanning work, so it is skipped along with the remaining files and
        // the job goes straight to synthesis of what was reviewed.
        IReadOnlyList<CandidateReviewFinding> prWideCandidates = budgetSoftCap.SoftCapped
            ? []
            : await this.RunPrWideScopePassesAsync(job, pr, baseContext, ct);

        if (dispatchResult.Exceptions.Count > 0)
        {
            // Attempt synthesis for the files that did succeed before propagating the partial failure.
            // This ensures that results from successfully-reviewed files are available (e.g. for posting
            // on the final retry) even when some files could not be reviewed.
            ReviewResult? partialResult = null;
            try
            {
                partialResult = await this.SynthesizeResultsAsync(job, pr, baseContext, effectiveClient, prWideCandidates, budgetSoftCap, ct);
            }
            catch (Exception ex)
            {
                LogSynthesisFailed(logger, job.Id, ex);
            }

            throw new PartialReviewFailureException(dispatchResult.Exceptions.Count, pr.ChangedFiles.Count, dispatchResult.Exceptions, partialResult);
        }

        return await this.SynthesizeResultsAsync(job, pr, baseContext, effectiveClient, prWideCandidates, budgetSoftCap, ct);
    }

    // Runs each pr_wide-scope entry in the client's review-pass list once at the job level. Each entry resolves to
    // its own configured model via the by-model seam (the same seam per-file passes use); an entry whose model
    // cannot be resolved is skipped with a trace and never falls back to another connection, while the remaining
    // entries still run. Returns the collected candidates, which the caller threads into synthesis. Returns an empty
    // list when there is no generator, no resolver, or no pr_wide entry — leaving a review without one byte-identical.
    private async Task<IReadOnlyList<CandidateReviewFinding>> RunPrWideScopePassesAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        CancellationToken ct)
    {
        if (prWideCandidateGeneratorFactory is null)
        {
            return [];
        }

        var prWidePasses = baseContext.ReviewPasses
            .Select((pass, ordinal) => (pass, ordinal))
            .Where(entry => string.Equals(entry.pass.Scope, ReviewPassScope.PrWide, StringComparison.Ordinal))
            .ToList();
        if (prWidePasses.Count == 0)
        {
            return [];
        }

        var generator = prWideCandidateGeneratorFactory();
        if (generator is null || aiRuntimeResolver is null)
        {
            LogPrWideScopeUnavailable(logger, job.Id, generator is null, aiRuntimeResolver is null);
            return [];
        }

        var budget = new PrWideGenerationBudget(
            MaxInvestigations: 3,
            MaxToolCallsPerInvestigation: 3,
            MaxSeedFilesPerInvestigation: 5);

        var collected = new List<CandidateReviewFinding>();
        foreach (var (pass, ordinal) in prWidePasses)
        {
            IResolvedAiChatRuntime runtime;
            try
            {
                runtime = await aiRuntimeResolver.ResolveChatRuntimeForModelAsync(job.ClientId, pass.ConfiguredModelId, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Never fall back to another connection: skip this entry with a trace and let the rest run.
                LogPrWideScopePassModelUnresolved(logger, job.Id, pass.ConfiguredModelId, ordinal, ex);
                continue;
            }

            // The per-file baseline is pass 1, so a job-level entry numbers as its list ordinal plus two.
            var candidates = await generator.GenerateCandidatesAsync(job, pr, baseContext, runtime, budget, ordinal + 2, pass.Shadow, pass.ReasoningEffort, ct);

            // A shadow entry still runs and records its full generation trace plus a shadow-completed event (both
            // inside the generator), but its candidates are never threaded into synthesis, so it never publishes.
            if (!pass.Shadow)
            {
                collected.AddRange(candidates);
            }
        }

        return collected;
    }

    private static IReviewPipeline<PerFileReviewContext> CreateDefaultPerFilePipeline(
        AiReviewOptions options,
        IProtocolRecorder protocolRecorder,
        IProRVPrefilter? proRvPrefilter,
        IAiConnectionRepository? aiConnectionRepository,
        IAiChatClientFactory? aiClientFactory,
        IAiRuntimeResolver? aiRuntimeResolver)
    {
        return new ReviewPipelineRunner<PerFileReviewContext>(
        [
            new FileByFileContextPrefetchStage(options, protocolRecorder),
            new FileByFileRiskMarkerStage(),
            new FileByFileConfidenceFloorStage(options, protocolRecorder),
            new FileByFileSemanticScreeningStage(
                new EmbeddingSemanticCommentScreener(
                    Microsoft.Extensions.Options.Options.Create(options),
                    aiRuntimeResolver,
                    NullLogger<EmbeddingSemanticCommentScreener>.Instance),
                protocolRecorder),
            new FileByFileInfoCommentStripStage(protocolRecorder),
            new FileByFileImportanceRankingStage(options),
            new FileByFileSelfReflectionRankingStage(options, NullLogger<FileByFileSelfReflectionRankingStage>.Instance),
        ]);
    }

    private async Task<ReviewResult> SynthesizeResultsAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        IReadOnlyList<CandidateReviewFinding> prWideCandidates,
        FileReviewDispatchPlanner.BudgetSoftCapSummary budgetSoftCap,
        CancellationToken ct)
    {
        return await this.GetSynthesisExecutor().SynthesizeAsync(job, pr, baseContext, effectiveClient, prWideCandidates, budgetSoftCap, ct);
    }

    private async Task RecordReviewProfileSelectedEventAsync(
        ReviewJob job,
        ReviewSystemContext baseContext,
        CancellationToken ct)
    {
        if (!baseContext.ActiveProtocolId.HasValue || baseContext.ProtocolRecorder is null)
        {
            return;
        }

        await baseContext.ProtocolRecorder.RecordReviewStrategyEventAsync(
            baseContext.ActiveProtocolId.Value,
            ReviewProtocolEventNames.ReviewProfileSelected,
            JsonSerializer.Serialize(
                new
                {
                    reviewKind = "file_by_file",
                }),
            JsonSerializer.Serialize(
                new
                {
                    profileId = job.ReviewPipelineProfileId ?? ReviewPipelineProfileCatalog.FileByFileBalancedProfileId,
                    isExplicit = !string.IsNullOrWhiteSpace(job.ReviewPipelineProfileId),
                }),
            null,
            ct);
    }

    internal static bool TryParseSynthesisResponse(
        string? responseText,
        out string summary,
        out IReadOnlyList<CandidateReviewFinding> crossCuttingComments)
    {
        return SynthesisResponseParser.TryParse(responseText, out summary, out crossCuttingComments);
    }

    internal static string BuildPerFileFindingId(ReviewFileResult fileResult, int ordinal)
    {
        return $"finding-pf-{fileResult.Id:N}-{ordinal:D3}";
    }

    internal static string DetermineCategory(ReviewComment comment)
    {
        if (comment.Message.StartsWith("consider ", StringComparison.OrdinalIgnoreCase) ||
            comment.Message.Contains("you could also", StringComparison.OrdinalIgnoreCase) ||
            comment.Message.Contains("you might want to", StringComparison.OrdinalIgnoreCase))
        {
            return "non_actionable";
        }

        return CandidateReviewFinding.PerFileCommentCategory;
    }

    internal static ReviewComment CreateReviewComment(string? filePath, int? lineNumber, CommentSeverity severity, string message)
    {
        return new ReviewComment(filePath, NormalizeLineNumber(lineNumber), severity, message);
    }

    internal static int? NormalizeLineNumber(int? lineNumber)
    {
        return lineNumber is > 0 ? lineNumber : null;
    }

    /// <summary>
    ///     Parses the JSON response from the cross-file quality-filter AI call.
    ///     Returns an empty list on any parse failure (fallback: keep original comments).
    /// </summary>
    internal static List<ReviewComment> ParseQualityFilterResponse(string responseText)
    {
        return QualityFilterExecutor.ParseResponse(responseText);
    }

    /// <summary>
    ///     Runs the cross-file quality-filter AI pass on <paramref name="comments" />.
    ///     If the AI call fails or returns an empty list, falls back to the original comments.
    /// </summary>
    internal async Task<List<ReviewComment>> RunQualityFilterAsync(
        Guid jobId,
        List<ReviewComment> comments,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        return await this.GetQualityFilterExecutor().ApplyAsync(jobId, comments, baseContext, effectiveClient, ct);
    }

    private FileReviewDispatchPlanner GetDispatchPlanner()
    {
        return fileReviewDispatchPlanner ??= new FileReviewDispatchPlanner(jobRepository, protocolRecorder, fileReviewer, this._opts, logger);
    }

    private ReviewSynthesisExecutor GetSynthesisExecutor()
    {
        return reviewSynthesisExecutor ??= new ReviewSynthesisExecutor(
            jobRepository,
            protocolRecorder,
            logger,
            this._opts,
            this.GetCandidateFindingFactory(),
            this.GetQualityFilterExecutor(),
            prLevelReviewVerificationExecutor,
            deterministicReviewFindingGate,
            reviewInvariantFactProviders,
            summaryReconciliationService,
            aiConnectionRepository,
            aiClientFactory,
            aiRuntimeResolver,
            chatClient,
            new SemanticFindingDeduplicator(new AiFindingMergeJudge(aiRuntimeResolver)),
            new ReviewFindingFinalizationPipeline([new RereadFinalizationCheck()], protocolRecorder));
    }

    private CandidateFindingFactory GetCandidateFindingFactory()
    {
        return candidateFindingFactory ??= new CandidateFindingFactory(reviewClaimExtractor);
    }

    private QualityFilterExecutor GetQualityFilterExecutor()
    {
        return qualityFilterExecutor ??= new QualityFilterExecutor(this._opts, logger);
    }

    /// <summary>
    ///     Classifies a changed file into a complexity tier based on changed-line count.<br />
    ///     Thresholds: ≤30 → Low; ≤150 → Medium; &gt;150 → High.
    /// </summary>
    public static FileComplexityTier ClassifyTier(ChangedFile file)
    {
        var lines = CountChangedLines(file.UnifiedDiff);
        return lines switch
        {
            <= 30 => FileComplexityTier.Low,
            <= 150 => FileComplexityTier.Medium,
            _ => FileComplexityTier.High,
        };
    }

    /// <summary>
    ///     Counts the number of added (+) or removed (-) lines in a unified diff.
    ///     Context lines and diff-header lines (<c>@@</c>, <c>---</c>, <c>+++</c>) are excluded.
    /// </summary>
    public static int CountChangedLines(string? diff)
    {
        if (string.IsNullOrEmpty(diff))
        {
            return 0;
        }

        var count = 0;
        foreach (var line in diff.AsSpan().EnumerateLines())
        {
            if (line.Length == 0)
            {
                continue;
            }

            var first = line[0];
            if (first is '+' or '-')
            {
                // Exclude +++ / --- diff-header lines
                if (line.Length >= 3 && line[1] == first && line[2] == first)
                {
                    continue;
                }

                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     Removes all <see cref="CommentSeverity.Info" /> entries from the comment list.
    ///     INFO observations belong in the narrative summary, not as actionable threads.
    /// </summary>
    internal static ReviewResult StripInfoComments(ReviewResult result)
    {
        if (result.Comments.Count == 0)
        {
            return result;
        }

        var filtered = result.Comments
            .Where(c => c.Severity != CommentSeverity.Info)
            .ToList()
            .AsReadOnly();

        return filtered.Count == result.Comments.Count
            ? result
            : result with { Comments = filtered };
    }

    /// <summary>
    ///     Applies confidence-gated severity downgrade using the reviewer's final confidence score
    ///     from the agentic loop.
    ///     <list type="bullet">
    ///         <item>confidence &lt; <see cref="AiReviewOptions.ConfidenceFloorError" /> → ERROR becomes WARNING</item>
    ///         <item>confidence &lt; <see cref="AiReviewOptions.ConfidenceFloorWarning" /> → WARNING becomes SUGGESTION</item>
    ///     </list>
    ///     Downgrade is skipped when <paramref name="finalConfidence" /> is <see langword="null" />.
    /// </summary>
    internal static ReviewResult ApplyConfidenceFloor(ReviewResult result, int? finalConfidence, AiReviewOptions opts)
    {
        if (finalConfidence is null || result.Comments.Count == 0)
        {
            return result;
        }

        var confidence = finalConfidence.Value;
        var adjusted = result.Comments
            .Select(c =>
            {
                var sev = c.Severity;
                if (sev == CommentSeverity.Error && confidence < opts.ConfidenceFloorError)
                {
                    sev = CommentSeverity.Warning;
                }
                else if (sev == CommentSeverity.Warning && confidence < opts.ConfidenceFloorWarning)
                {
                    sev = CommentSeverity.Suggestion;
                }

                return sev == c.Severity ? c : CreateReviewComment(c.FilePath, c.LineNumber, sev, c.Message);
            })
            .ToList()
            .AsReadOnly();

        return adjusted.SequenceEqual(result.Comments)
            ? result
            : result with { Comments = adjusted };
    }
}
