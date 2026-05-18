// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using System.Text.Json;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using MeisterProPR.ProRV.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;

internal sealed class AgenticFileByFileReviewOrchestrator(
    IProtocolRecorder protocolRecorder,
    IJobRepository jobRepository,
    IChatClient? chatClient,
    IOptions<AiReviewOptions> options,
    ILogger<AgenticFileByFileReviewOrchestrator> logger,
    AgenticFileReviewer fileReviewer,
    AgenticFileReviewDispatchPlanner? fileReviewDispatchPlanner = null,
    AgenticReviewSynthesisExecutor? reviewSynthesisExecutor = null,
    AgenticCandidateFindingFactory? candidateFindingFactory = null,
    QualityFilterExecutor? qualityFilterExecutor = null,
    PrLevelReviewVerificationExecutor? prLevelReviewVerificationExecutor = null,
    IAiConnectionRepository? aiConnectionRepository = null,
    IAiChatClientFactory? aiClientFactory = null,
    IAiRuntimeResolver? aiRuntimeResolver = null,
    IDeterministicReviewFindingGate? deterministicReviewFindingGate = null,
    IEnumerable<IReviewInvariantFactProvider>? reviewInvariantFactProviders = null,
    IReviewClaimExtractor? reviewClaimExtractor = null,
    ISummaryReconciliationService? summaryReconciliationService = null) : IAgenticFileByFileReviewOrchestrator
{
    private static readonly ImmutableArray<string> HedgePhrases =
    [
        "if your ", "if the file", "if [", "please verify", "validate that",
        "consider whether", "this may be", "this could be", "you may want to",
        "worth checking", "it appears", "it seems", "i cannot confirm",
        "unclear whether", "worth verifying", "if applicable",
    ];

    private static readonly ImmutableArray<string> VagueSuggestionPhrases =
    [
        "consider refactoring", "consider adding", "you could also", "you might also",
        "you might want to", "it would be worth", "would also be good",
        "could be strengthened", "could be made", "could also verify",
    ];

    private readonly AiReviewOptions _opts = options.Value;

    public AgenticFileByFileReviewOrchestrator(
        IAiReviewCore aiCore,
        IProtocolRecorder protocolRecorder,
        IJobRepository jobRepository,
        IChatClient? chatClient,
        IOptions<AiReviewOptions> options,
        ILogger<AgenticFileByFileReviewOrchestrator> logger,
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
        IProRVPrefilter? proRvPrefilter = null)
        : this(
            protocolRecorder,
            jobRepository,
            chatClient,
            options,
            logger,
            new AgenticFileReviewer(
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
                pipelineProfileProvider),
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
            summaryReconciliationService)
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
            ?? throw new InvalidOperationException("No chat client available for agentic file review orchestration.");

        if (baseContext.ActiveProtocolId.HasValue && baseContext.ProtocolRecorder is not null)
        {
            await baseContext.ProtocolRecorder.RecordReviewStrategyEventAsync(
                baseContext.ActiveProtocolId.Value,
                ReviewProtocolEventNames.ReviewStrategySelected,
                JsonSerializer.Serialize(
                    new
                    {
                        strategy = "agentic_file_by_file",
                        jobId = job.Id,
                        selectionSource = job.ReviewStrategySelectionSource.ToString(),
                    }),
                JsonSerializer.Serialize(
                    new
                    {
                        strategy = job.ReviewStrategy.ToString(),
                        orchestrator = nameof(AgenticFileByFileReviewOrchestrator),
                    }),
                null,
                ct);
        }

        var dispatchResult = this.GetDispatchPlanner().ExecuteAsync(job, pr, baseContext, effectiveClient, ct);
        var result = await dispatchResult;
        await this.RecordLateSteeringBaselinePassEventAsync(baseContext, pr, result.Exceptions.Count, ct);
        var augmentationFindings = await this.BuildAugmentationFindingsAsync(job, pr, baseContext, effectiveClient, ct);
        await this.RecordLateSteeringAugmentationPassEventAsync(baseContext, pr, augmentationFindings.Count, ct);

        if (result.Exceptions.Count > 0)
        {
            ReviewResult? partialResult = null;
            try
            {
                partialResult = await this.SynthesizeResultsAsync(
                    job, pr, baseContext, effectiveClient, result.AgenticCandidateFindings, augmentationFindings, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agentic synthesis failed for job {JobId}", job.Id);
            }

            throw new PartialReviewFailureException(result.Exceptions.Count, pr.ChangedFiles.Count, result.Exceptions, partialResult);
        }

        return await this.SynthesizeResultsAsync(job, pr, baseContext, effectiveClient, result.AgenticCandidateFindings, augmentationFindings, ct);
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
            new AgenticProRvPrefilterStage(
                protocolRecorder,
                proRvPrefilter,
                aiConnectionRepository,
                aiClientFactory,
                aiRuntimeResolver,
                NullLogger<AgenticProRvPrefilterStage>.Instance),
            new AgenticConfidenceFloorStage(options),
            new AgenticSpeculativeCommentFilterStage(),
            new AgenticInfoCommentStripStage(),
            new AgenticVagueSuggestionFilterStage(),
        ]);
    }

    private async Task<ReviewResult> SynthesizeResultsAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        IReadOnlyList<CandidateReviewFinding> agenticCandidateFindings,
        IReadOnlyList<CandidateReviewFinding> augmentationFindings,
        CancellationToken ct)
    {
        return await this.GetSynthesisExecutor().SynthesizeAsync(job, pr, baseContext, effectiveClient, agenticCandidateFindings, augmentationFindings, ct);
    }

    private async Task<IReadOnlyList<CandidateReviewFinding>> BuildAugmentationFindingsAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        if (baseContext.AugmentationMode != ReviewAugmentationMode.LateAugmentation)
        {
            return [];
        }

        var filesToReview = pr.ChangedFiles
            .Where(file => !baseContext.ExclusionRules.Matches(file.Path))
            .ToList();
        if (filesToReview.Count == 0)
        {
            return [];
        }

        var findings = new List<CandidateReviewFinding>();
        for (var index = 0; index < filesToReview.Count; index++)
        {
            var file = filesToReview[index];
            var augmentationResult = await fileReviewer.ReviewAugmentationAsync(
                job,
                pr,
                file,
                index + 1,
                filesToReview.Count,
                baseContext,
                effectiveClient,
                ct);
            if (augmentationResult.Comments.Count == 0)
            {
                continue;
            }

            var transientResult = new ReviewFileResult(job.Id, file.Path);
            transientResult.MarkCompleted(augmentationResult.Summary, augmentationResult.Comments);
            findings.AddRange(this.GetCandidateFindingFactory().Build([transientResult], passKind: ReviewPassKind.ProRVAugmentation));
        }

        return findings;
    }

    private async Task RecordLateSteeringBaselinePassEventAsync(
        ReviewSystemContext baseContext,
        PullRequest pr,
        int failedFileCount,
        CancellationToken ct)
    {
        if (baseContext.AugmentationMode != ReviewAugmentationMode.LateAugmentation ||
            !baseContext.ActiveProtocolId.HasValue ||
            baseContext.ProtocolRecorder is null)
        {
            return;
        }

        await baseContext.ProtocolRecorder.RecordReviewStrategyEventAsync(
            baseContext.ActiveProtocolId.Value,
            ReviewProtocolEventNames.LateSteeringBaselinePassCompleted,
            JsonSerializer.Serialize(
                new
                {
                    augmentationMode = baseContext.AugmentationMode.ToString(),
                    passKind = ReviewPassKind.Baseline.ToString(),
                    proRvEnabled = false,
                    scopedFileCount = CountFilesInScope(pr, baseContext),
                }),
            JsonSerializer.Serialize(
                new
                {
                    completed = failedFileCount == 0,
                    failedFileCount,
                }),
            null,
            ct);
    }

    private async Task RecordLateSteeringAugmentationPassEventAsync(
        ReviewSystemContext baseContext,
        PullRequest pr,
        int augmentationCandidateCount,
        CancellationToken ct)
    {
        if (baseContext.AugmentationMode != ReviewAugmentationMode.LateAugmentation ||
            !baseContext.ActiveProtocolId.HasValue ||
            baseContext.ProtocolRecorder is null)
        {
            return;
        }

        await baseContext.ProtocolRecorder.RecordReviewStrategyEventAsync(
            baseContext.ActiveProtocolId.Value,
            ReviewProtocolEventNames.LateSteeringAugmentationPassCompleted,
            JsonSerializer.Serialize(
                new
                {
                    augmentationMode = baseContext.AugmentationMode.ToString(),
                    passKind = ReviewPassKind.ProRVAugmentation.ToString(),
                    proRvEnabled = true,
                    scopedFileCount = CountFilesInScope(pr, baseContext),
                }),
            JsonSerializer.Serialize(
                new
                {
                    augmentationCandidateCount,
                }),
            null,
            ct);
    }

    private static int CountFilesInScope(PullRequest pr, ReviewSystemContext baseContext)
    {
        return pr.ChangedFiles.Count(file => !baseContext.ExclusionRules.Matches(file.Path));
    }

    private AgenticFileReviewDispatchPlanner GetDispatchPlanner()
    {
        return fileReviewDispatchPlanner ??= new AgenticFileReviewDispatchPlanner(jobRepository, protocolRecorder, fileReviewer, this._opts, logger);
    }

    private AgenticReviewSynthesisExecutor GetSynthesisExecutor()
    {
        return reviewSynthesisExecutor ??= new AgenticReviewSynthesisExecutor(
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
            chatClient);
    }

    private AgenticCandidateFindingFactory GetCandidateFindingFactory()
    {
        return candidateFindingFactory ??= new AgenticCandidateFindingFactory(reviewClaimExtractor);
    }

    private QualityFilterExecutor GetQualityFilterExecutor()
    {
        return qualityFilterExecutor ??= new QualityFilterExecutor(this._opts, NullLogger<FileByFileReviewOrchestrator>.Instance);
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
                if (line.Length >= 3 && line[1] == first && line[2] == first)
                {
                    continue;
                }

                count++;
            }
        }

        return count;
    }

    internal static ReviewResult FilterSpeculativeComments(ReviewResult result)
    {
        if (result.Comments.Count == 0)
        {
            return result;
        }

        var filtered = result.Comments
            .Where(c =>
            {
                foreach (var phrase in HedgePhrases)
                {
                    if (c.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            })
            .ToList()
            .AsReadOnly();

        return filtered.Count == result.Comments.Count
            ? result
            : result with { Comments = filtered };
    }

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

    internal static ReviewResult FilterVagueSuggestions(ReviewResult result)
    {
        if (result.Comments.Count == 0)
        {
            return result;
        }

        var filtered = result.Comments
            .Where(c =>
            {
                if (c.Severity != CommentSeverity.Suggestion)
                {
                    return true;
                }

                foreach (var phrase in VagueSuggestionPhrases)
                {
                    if (c.Message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            })
            .ToList()
            .AsReadOnly();

        return filtered.Count == result.Comments.Count
            ? result
            : result with { Comments = filtered };
    }

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
