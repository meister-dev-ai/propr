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
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
    ISummaryReconciliationService? summaryReconciliationService = null) : IFileByFileReviewOrchestrator
{
    private static readonly JsonSerializerOptions FinalGateJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Phrases indicating the reviewer is guessing rather than confirming a finding.
    ///     A comment containing any of these is speculative and must be discarded.
    /// </summary>
    private static readonly ImmutableArray<string> HedgePhrases =
    [
        "if your ", "if the file", "if [", "please verify", "validate that",
        "consider whether", "this may be", "this could be", "you may want to",
        "worth checking", "it appears", "it seems", "i cannot confirm",
        "unclear whether", "worth verifying", "if applicable",
    ];

    /// <summary>
    ///     Vague action phrases applied only to <see cref="CommentSeverity.Suggestion" /> entries.
    ///     A suggestion containing any of these does not name a specific, actionable alternative.
    /// </summary>
    private static readonly ImmutableArray<string> VagueSuggestionPhrases =
    [
        "consider refactoring", "consider adding", "you could also", "you might also",
        "you might want to", "it would be worth", "would also be good",
        "could be strengthened", "could be made", "could also verify",
    ];

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
        IReviewPipeline<PerFileReviewContext>? perFilePipeline = null)
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
                perFilePipeline,
                aiConnectionRepository,
                aiClientFactory,
                memoryService,
                aiRuntimeResolver,
                new CommentRelevanceFilterExecutor(commentRelevanceFilterRegistry, protocolRecorder),
                reviewInvariantFactProviders,
                new LocalReviewVerificationExecutor(reviewClaimExtractor, reviewFindingVerifier, protocolRecorder)),
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
            ?? throw new InvalidOperationException("No chat client available for file review orchestration.");
        var dispatchResult = await this.GetDispatchPlanner().ExecuteAsync(job, pr, baseContext, effectiveClient, ct);

        if (dispatchResult.Exceptions.Count > 0)
        {
            // Attempt synthesis for the files that did succeed before propagating the partial failure.
            // This ensures that results from successfully-reviewed files are available (e.g. for posting
            // on the final retry) even when some files could not be reviewed.
            ReviewResult? partialResult = null;
            try
            {
                partialResult = await this.SynthesizeResultsAsync(job, pr, baseContext, effectiveClient, ct);
            }
            catch (Exception ex)
            {
                LogSynthesisFailed(logger, job.Id, ex);
            }

            throw new PartialReviewFailureException(dispatchResult.Exceptions.Count, pr.ChangedFiles.Count, dispatchResult.Exceptions, partialResult);
        }

        return await this.SynthesizeResultsAsync(job, pr, baseContext, effectiveClient, ct);
    }

    private async Task<ReviewResult> SynthesizeResultsAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        return await this.GetSynthesisExecutor().SynthesizeAsync(job, pr, baseContext, effectiveClient, ct);
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
            chatClient);
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
    ///     Discards any <see cref="ReviewComment" /> whose message contains a hedge phrase,
    ///     indicating the reviewer is speculating rather than confirming a finding (IMP-01, US1).
    /// </summary>
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

    /// <summary>
    ///     Removes all <see cref="CommentSeverity.Info" /> entries from the comment list (IMP-04, US2).
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
    ///     Discards <see cref="CommentSeverity.Suggestion" /> entries that contain vague action phrases
    ///     and do not provide a concrete, named alternative (IMP-05, US3).
    ///     WARNING and ERROR entries are not affected.
    /// </summary>
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

    /// <summary>
    ///     Applies confidence-gated severity downgrade using the reviewer's final confidence score
    ///     from the agentic loop (IMP-07, US5).
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
