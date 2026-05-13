// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AI.FileByFileReview;

internal sealed partial class FileByFileReviewOrchestrator(
    IProtocolRecorder protocolRecorder,
    IJobRepository jobRepository,
    IChatClient? chatClient,
    IOptions<AiReviewOptions> options,
    ILogger<FileByFileReviewOrchestrator> logger,
    FileReviewer fileReviewer,
    IAiConnectionRepository? aiConnectionRepository = null,
    IAiChatClientFactory? aiClientFactory = null,
    IAiRuntimeResolver? aiRuntimeResolver = null,
    IDeterministicReviewFindingGate? deterministicReviewFindingGate = null,
    IEnumerable<IReviewInvariantFactProvider>? reviewInvariantFactProviders = null,
    IReviewClaimExtractor? reviewClaimExtractor = null,
    IReviewEvidenceCollector? reviewEvidenceCollector = null,
    ISummaryReconciliationService? summaryReconciliationService = null) : IFileByFileReviewOrchestrator
{
    private static readonly JsonSerializerOptions FinalGateJsonOptions = new(JsonSerializerDefaults.Web);
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
        ISummaryReconciliationService? summaryReconciliationService = null)
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
            aiConnectionRepository,
            aiClientFactory,
            memoryService,
            aiRuntimeResolver,
            new CommentRelevanceFilterExecutor(commentRelevanceFilterRegistry, protocolRecorder),
            reviewInvariantFactProviders,
            reviewClaimExtractor,
            reviewFindingVerifier),
            aiConnectionRepository,
            aiClientFactory,
            aiRuntimeResolver,
            deterministicReviewFindingGate,
            reviewInvariantFactProviders,
            reviewClaimExtractor,
            reviewEvidenceCollector,
            summaryReconciliationService)
    {
    }

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

    public async Task<ReviewResult> ReviewAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        CancellationToken ct,
        IChatClient? overrideClient = null)
    {
        var effectiveClient = overrideClient ?? chatClient
            ?? throw new InvalidOperationException("No chat client available for file review orchestration.");
        var jobWithResults = await jobRepository.GetByIdWithFileResultsAsync(job.Id, ct) ?? job;

        // Build a map of ALL existing results (completed, failed, or interrupted)
        // so we can reuse them on retry instead of hitting the UNIQUE(job_id, file_path) constraint.
        var existingResults = jobWithResults.FileReviewResults.ToDictionary(r => r.FilePath);
        var completedFiles = existingResults
            .Where(kvp => kvp.Value.IsComplete)
            .Select(kvp => kvp.Key)
            .ToHashSet();

        var filesToReview = pr.ChangedFiles.Where(f => !completedFiles.Contains(f.Path)).ToList();

        // Exclude files matching repository exclusion rules before parallel dispatch.
        if (baseContext.ExclusionRules.HasPatterns)
        {
            var excluded = filesToReview.Where(f => baseContext.ExclusionRules.Matches(f.Path)).ToList();
            if (excluded.Count > 0)
            {
                foreach (var excludedFile in excluded)
                {
                    var existingExcluded = existingResults.GetValueOrDefault(excludedFile.Path);
                    await this.MarkFileExcludedAsync(job, excludedFile, baseContext, existingExcluded, ct);
                }

                filesToReview = filesToReview.Except(excluded).ToList();
            }
        }

        // Priority ordering: largest content first; deleted/binary files last; stable secondary sort by path.
        filesToReview =
        [
            .. filesToReview
                .OrderBy(f => f.IsBinary || f.ChangeType == ChangeType.Delete ? 1 : 0)
                .ThenByDescending(f => (f.FullContent?.Length ?? 0) + (f.UnifiedDiff?.Length ?? 0))
                .ThenBy(f => f.Path),
        ];

        var exceptions = new List<Exception>();

        if (filesToReview.Count > 0)
        {
            using var semaphore = new SemaphoreSlim(this._opts.MaxFileReviewConcurrency);

            // Precompute the ordered file list and an index map once — avoids repeated ToList() + IndexOf() per task.
            var allChangedFiles = pr.ChangedFiles.ToList();
            var fileIndexByPath = allChangedFiles
                .Select((f, i) => (f.Path, Index: i + 1))
                .ToDictionary(x => x.Path, x => x.Index);

            var tasks = filesToReview.Select(async file =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var fileIndex = fileIndexByPath.GetValueOrDefault(file.Path, 1);
                    var existingResult = existingResults.GetValueOrDefault(file.Path);
                    await fileReviewer.ReviewAsync(
                        job,
                        pr,
                        file,
                        fileIndex,
                        allChangedFiles.Count,
                        baseContext,
                        existingResult,
                        effectiveClient,
                        ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogFileReviewFailed(logger, file.Path, job.Id, ex);
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        if (exceptions.Count > 0)
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

            throw new PartialReviewFailureException(exceptions.Count, pr.ChangedFiles.Count, exceptions, partialResult);
        }

        return await this.SynthesizeResultsAsync(job, pr, baseContext, effectiveClient, ct);
    }

    private async Task MarkFileExcludedAsync(
        ReviewJob job,
        ChangedFile file,
        ReviewSystemContext context,
        ReviewFileResult? existingResult,
        CancellationToken ct)
    {
        var exclusionReason = context.ExclusionRules.GetMatchingPattern(file.Path) ?? "excluded";
        LogFileExcluded(logger, file.Path, exclusionReason, job.Id);

        ReviewFileResult fileResult;
        if (existingResult is { IsComplete: false })
        {
            // Reuse the existing row (interrupted or previously failed) and mark it excluded.
            // This avoids a UNIQUE(job_id, file_path) constraint violation on retry.
            existingResult.ResetForRetry();
            existingResult.MarkExcluded(exclusionReason);
            await jobRepository.UpdateFileResultAsync(existingResult, ct);
            fileResult = existingResult;
        }
        else
        {
            fileResult = new ReviewFileResult(job.Id, file.Path);
            fileResult.MarkExcluded(exclusionReason);
            await jobRepository.AddFileResultAsync(fileResult, ct);
        }

        Guid? protocolId = null;
        try
        {
            protocolId = await protocolRecorder.BeginAsync(
                job.Id,
                job.RetryCount + 1,
                file.Path,
                fileResult.Id,
                ct: ct);
        }
        catch (Exception ex)
        {
            LogProtocolBeginFailed(logger, file.Path, job.Id, ex);
        }

        if (protocolId.HasValue)
        {
            await protocolRecorder.SetCompletedAsync(protocolId.Value, "Excluded", 0, 0, 0, 0, null, ct);
        }
    }


    private async Task<ReviewResult> SynthesizeResultsAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        var jobWithResults = await jobRepository.GetByIdWithFileResultsAsync(job.Id, ct);
        var allResults = jobWithResults!.FileReviewResults;
        var freshResults = allResults
            .Where(r => !r.IsCarriedForward)
            .ToList();
        var carriedForwardCandidatesSkipped = allResults
            .Where(r => r.IsComplete && r.IsCarriedForward && r.Comments is not null)
            .Sum(r => r.Comments!.Count);

        var perFileSummaries = freshResults
            .Where(r => r.IsComplete && r.PerFileSummary != null)
            .Select(r => (r.FilePath, Summary: r.PerFileSummary!))
            .ToList();

        var allComments = freshResults
            .Where(r => r.IsComplete && r.Comments != null)
            .SelectMany(r => r.Comments!)
            .Select(NormalizeCommentAnchor)
            .ToList();

        // Synthesis AI call — resolve per-client connection the same way per-file reviews do
        AiConnectionDto? synthTierDto = null;
        string? synthesisModelId = null;
        if (aiRuntimeResolver is not null)
        {
            try
            {
                var synthesisRuntime = await aiRuntimeResolver.ResolveChatRuntimeAsync(
                    job.ClientId,
                    AiPurpose.ReviewHighEffort,
                    ct);
                synthTierDto = synthesisRuntime.Connection;
                effectiveClient = synthesisRuntime.ChatClient;
                synthesisModelId = synthesisRuntime.Model.RemoteModelId;
            }
            catch
            {
                synthTierDto = null;
                synthesisModelId = null;
            }
        }
        else if (aiConnectionRepository is not null && aiClientFactory is not null)
        {
            synthTierDto = await aiConnectionRepository.GetForTierAsync(job.ClientId, AiConnectionModelCategory.HighEffort, ct);
            if (synthTierDto is not null)
            {
                effectiveClient = aiClientFactory.CreateClient(synthTierDto.BaseUrl, synthTierDto.Secret);
                synthesisModelId = synthTierDto.GetBoundModelId(AiPurpose.ReviewHighEffort)
                                   ?? synthTierDto.ConfiguredModels.FirstOrDefault(model => model.SupportsChat)?.RemoteModelId;
            }
        }

        baseContext.ModelId = synthesisModelId
                              ?? baseContext.ModelId
                              ?? job.AiModel
                              ?? this._opts.ModelId;

        LogSynthesisStarted(logger, job.Id);

        Guid? protocolId = null;
        try
        {
            protocolId = await protocolRecorder.BeginAsync(
                job.Id,
                job.RetryCount + 1,
                "synthesis",
                null,
                AiConnectionModelCategory.HighEffort,
                synthesisModelId,
                ct);
        }
        catch (Exception ex)
        {
            LogProtocolBeginFailed(logger, "synthesis", job.Id, ex);
        }

        string finalSummary;
        IReadOnlyList<CandidateReviewFinding> synthesizedFindings = [];
        string? synthesisInputSample = null;
        string? synthesisSystemPrompt = null;
        try
        {
            var expectsJson = allComments.Count > 0;
            var perFileCandidateFindings = BuildPerFileCandidateFindings(freshResults, reviewClaimExtractor: reviewClaimExtractor);
            var systemPrompt = ReviewPrompts.BuildSynthesisSystemPrompt(baseContext, expectsJson);
            synthesisSystemPrompt = systemPrompt;
            var userMessage = ReviewPrompts.BuildSynthesisUserMessage(
                perFileSummaries,
                pr.Title,
                pr.Description,
                allComments,
                perFileCandidateFindings);
            synthesisInputSample = userMessage;
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userMessage),
            };

            var response = await effectiveClient.GetResponseAsync(
                messages,
                new ChatOptions { ModelId = baseContext.ModelId, Temperature = baseContext.Temperature },
                ct);

            var responseText = response.Text ?? string.Empty;
            var totalInputTokens = response.Usage?.InputTokenCount ?? 0;
            var totalOutputTokens = response.Usage?.OutputTokenCount ?? 0;

            if (TryParseSynthesisResponse(responseText, out var parsedSummary, out var parsedCrossCuttingFindings))
            {
                finalSummary = parsedSummary;
                synthesizedFindings = parsedCrossCuttingFindings;
            }
            else if (expectsJson && LooksLikeJsonObject(responseText))
            {
                LogSynthesisJsonRepairStarted(logger, job.Id);

                var repairMessages = new List<ChatMessage>(messages)
                {
                    new(ChatRole.Assistant, responseText),
                    new(ChatRole.User, BuildSynthesisJsonRepairPrompt()),
                };

                var repairResponse = await effectiveClient.GetResponseAsync(
                    repairMessages,
                    new ChatOptions { ModelId = baseContext.ModelId, Temperature = baseContext.Temperature },
                    ct);

                totalInputTokens += repairResponse.Usage?.InputTokenCount ?? 0;
                totalOutputTokens += repairResponse.Usage?.OutputTokenCount ?? 0;

                var repairedText = repairResponse.Text ?? string.Empty;
                if (TryParseSynthesisResponse(repairedText, out parsedSummary, out parsedCrossCuttingFindings))
                {
                    finalSummary = parsedSummary;
                    synthesizedFindings = parsedCrossCuttingFindings;
                    LogSynthesisJsonRepairSucceeded(logger, job.Id);
                }
                else
                {
                    finalSummary = string.Join("\n\n", perFileSummaries.Select(s => $"## {s.FilePath}\n{s.Summary}"));
                    synthesizedFindings = [];
                    LogSynthesisJsonRepairFailed(logger, job.Id);
                }
            }
            else
            {
                finalSummary = responseText;
                synthesizedFindings = [];
            }

            if (string.IsNullOrWhiteSpace(finalSummary))
            {
                finalSummary = string.Join("\n\n", perFileSummaries.Select(s => $"## {s.FilePath}\n{s.Summary}"));
            }

            if (protocolId.HasValue)
            {
                await protocolRecorder.RecordAiCallAsync(
                    protocolId.Value,
                    1,
                    totalInputTokens,
                    totalOutputTokens,
                    userMessage,
                    systemPrompt,
                    finalSummary,
                    ct);

                await protocolRecorder.SetCompletedAsync(
                    protocolId.Value,
                    "Completed",
                    totalInputTokens,
                    totalOutputTokens,
                    1,
                    0,
                    null,
                    ct);
            }

            LogSynthesisCompleted(logger, job.Id);
        }
        catch (Exception ex)
        {
            LogSynthesisFailed(logger, job.Id, ex);
            finalSummary = string.Join("\n\n", perFileSummaries.Select(s => $"## {s.FilePath}\n{s.Summary}"));
            synthesizedFindings = [];

            if (protocolId.HasValue)
            {
                await protocolRecorder.RecordAiCallAsync(
                    protocolId.Value,
                    1,
                    0,
                    0,
                    synthesisInputSample,
                    synthesisSystemPrompt,
                    null,
                    ct,
                    "ai_call_synthesis_failed",
                    ex.Message);

                await protocolRecorder.SetCompletedAsync(protocolId.Value, "Failed", 0, 0, 0, 0, null, ct);
            }
        }

        // US2: Deduplicate cross-file findings (moved from ReviewOrchestrationService → here)
        var deduped = FindingDeduplicator.Deduplicate(allComments).ToList();

        // IMP-08: Run quality-filter AI pass when comment volume exceeds the threshold
        if (deduped.Count >= this._opts.QualityFilterThreshold)
        {
            deduped = await this.RunQualityFilterAsync(job.Id, deduped, baseContext, effectiveClient, ct);
        }

        var gate = deterministicReviewFindingGate;
        if (gate is null)
        {
            var combinedComments = synthesizedFindings.Count > 0
                ? AssignSynthesisFindingIds(synthesizedFindings)
                    .Select(finding => CreateReviewComment(finding.FilePath, finding.LineNumber, finding.Severity, finding.Message))
                    .Concat(deduped)
                    .ToList()
                : (IReadOnlyList<ReviewComment>)deduped;

            LogCrossCuttingConcernsFound(logger, synthesizedFindings.Count, job.Id);
            return new ReviewResult(finalSummary, combinedComments)
            {
                CarriedForwardCandidatesSkipped = carriedForwardCandidatesSkipped,
            };
        }

        var prLevelFindings = await this.VerifyPrLevelFindingsAsync(
            AssignSynthesisFindingIds(synthesizedFindings),
            baseContext,
            pr.SourceBranch,
            protocolId,
            ct);

        var candidateFindings = BuildPerFileCandidateFindings(freshResults, deduped, reviewClaimExtractor)
            .Concat(prLevelFindings)
            .ToList();

        var invariantFacts = reviewInvariantFactProviders?
                                 .SelectMany(provider => provider.GetFacts())
                                 .ToList()
                             ?? [];
        var gateDecisions = await gate.EvaluateAsync(candidateFindings, invariantFacts, ct);
        var reconciler = summaryReconciliationService ?? new SummaryReconciliationService();
        var reconciliation = reconciler.Reconcile(finalSummary, candidateFindings, gateDecisions);

        if (protocolId.HasValue)
        {
            await this.RecordFinalGateProtocolAsync(protocolId.Value, candidateFindings, gateDecisions, reconciliation, ct);
            await protocolRecorder.RecordVerificationEventAsync(
                protocolId.Value,
                ReviewProtocolEventNames.SummaryReconciliation,
                JsonSerializer.Serialize(
                    new
                    {
                        rewritePerformed = reconciliation.RewritePerformed,
                        droppedCount = reconciliation.DroppedFindingIds.Count,
                        summaryOnlyCount = reconciliation.SummaryOnlyFindingIds.Count,
                    }),
                JsonSerializer.Serialize(reconciliation, FinalGateJsonOptions),
                null,
                ct);
        }

        var publishedComments = MaterializePublishedComments(candidateFindings, gateDecisions);
        finalSummary = reconciliation.FinalSummary;

        LogCrossCuttingConcernsFound(logger, synthesizedFindings.Count, job.Id);
        return new ReviewResult(finalSummary, publishedComments)
        {
            CarriedForwardCandidatesSkipped = carriedForwardCandidatesSkipped,
        };
    }


    private static IReadOnlyList<CandidateReviewFinding> ParseCrossCuttingConcerns(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(StripMarkdownCodeFences(responseText));
            if (!doc.RootElement.TryGetProperty("cross_cutting_concerns", out var concernsEl) ||
                concernsEl.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<CandidateReviewFinding>();
            foreach (var item in concernsEl.EnumerateArray())
            {
                var message = item.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                var severity = CommentSeverity.Warning;
                if (item.TryGetProperty("severity", out var sevEl))
                {
                    var sevStr = sevEl.GetString() ?? string.Empty;
                    severity = sevStr.ToLowerInvariant() switch
                    {
                        "error" => CommentSeverity.Error,
                        "info" => CommentSeverity.Info,
                        "suggestion" => CommentSeverity.Suggestion,
                        _ => CommentSeverity.Warning,
                    };
                }

                var category = item.TryGetProperty("category", out var categoryEl)
                    ? categoryEl.GetString()
                    : null;
                category = string.IsNullOrWhiteSpace(category)
                    ? CandidateReviewFinding.CrossCuttingCategory
                    : category;

                var summaryText = item.TryGetProperty("candidateSummaryText", out var summaryEl)
                    ? summaryEl.GetString()
                    : null;
                var evidence = ParseEvidenceReference(item);

                result.Add(
                    new CandidateReviewFinding(
                        $"finding-cc-unassigned-{result.Count + 1:D3}",
                        new CandidateFindingProvenance(
                            CandidateFindingProvenance.SynthesizedCrossCuttingOrigin,
                            "synthesis"),
                        severity,
                        message,
                        category,
                        evidence: evidence,
                        candidateSummaryText: summaryText));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryParsePrVerificationResponse(
        string? responseText,
        ClaimDescriptor claim,
        out VerificationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(claim);

        outcome = VerificationOutcome.DegradedUnresolved(
            claim,
            VerificationOutcome.AiMicroVerifierEvaluator,
            ReviewFindingGateReasonCodes.VerificationDegraded,
            "AI micro-verification degraded: response could not be parsed.");

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(StripMarkdownCodeFences(responseText));
            var root = doc.RootElement;

            if (!root.TryGetProperty("verdict", out var verdictEl) ||
                !root.TryGetProperty("recommended_disposition", out var dispositionEl))
            {
                return false;
            }

            var verdict = verdictEl.GetString();
            var disposition = dispositionEl.GetString();
            if (string.IsNullOrWhiteSpace(verdict) || string.IsNullOrWhiteSpace(disposition))
            {
                return false;
            }

            var reasonCodes = root.TryGetProperty("reason_codes", out var reasonCodesEl) && reasonCodesEl.ValueKind == JsonValueKind.Array
                ? reasonCodesEl.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray()
                : [];

            if (reasonCodes.Length == 0)
            {
                reasonCodes =
                [
                    string.Equals(disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal)
                        ? ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport
                        : ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport,
                ];
            }

            var summary = root.TryGetProperty("summary", out var summaryEl)
                ? summaryEl.GetString()
                : null;
            var normalizedDisposition = string.Equals(disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal)
                ? FinalGateDecision.PublishDisposition
                : FinalGateDecision.SummaryOnlyDisposition;
            var normalizedVerdict = string.Equals(verdict, "supported", StringComparison.OrdinalIgnoreCase)
                ? VerificationOutcome.SupportedKind
                : VerificationOutcome.UnresolvedKind;
            var evidenceStrength = string.Equals(normalizedDisposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal)
                ? VerificationOutcome.StrongEvidence
                : VerificationOutcome.WeakEvidence;

            outcome = new VerificationOutcome(
                claim.ClaimId,
                claim.FindingId,
                normalizedVerdict,
                normalizedDisposition,
                reasonCodes,
                [],
                evidenceStrength,
                summary,
                VerificationOutcome.AiMicroVerifierEvaluator,
                false);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseSynthesisResponse(
        string? responseText,
        out string summary,
        out IReadOnlyList<CandidateReviewFinding> crossCuttingComments)
    {
        summary = string.Empty;
        crossCuttingComments = [];

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return false;
        }

        var trimmed = StripMarkdownCodeFences(responseText);

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (!doc.RootElement.TryGetProperty("summary", out var summaryEl))
            {
                return false;
            }

            summary = summaryEl.ValueKind == JsonValueKind.String
                ? summaryEl.GetString() ?? string.Empty
                : summaryEl.GetRawText();
            crossCuttingComments = ParseCrossCuttingConcerns(trimmed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BuildSynthesisJsonRepairPrompt()
    {
        return """
               Your previous response was not valid JSON.
               Reformat it now as a single raw JSON object with exactly these keys:
               - "summary": string
               - "cross_cutting_concerns": array of objects with keys "message", "severity", "category", "candidateSummaryText", "supportingFindingIds", "supportingFiles", "evidenceResolutionState", and "evidenceSource"

               Escape any quotes inside string values correctly.
               Do NOT use markdown fences.
               Do NOT add any prose before or after the JSON.
               The first character must be '{' and the last character must be '}'.
               """;
    }

    private static EvidenceReference ParseEvidenceReference(JsonElement item)
    {
        var supportingFindingIds = item.TryGetProperty("supportingFindingIds", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array
            ? idsEl.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray()
            : [];

        var supportingFiles = item.TryGetProperty("supportingFiles", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array
            ? filesEl.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray()
            : [];

        var evidenceResolutionState = item.TryGetProperty("evidenceResolutionState", out var stateEl)
            ? stateEl.GetString()
            : null;
        var evidenceSource = item.TryGetProperty("evidenceSource", out var sourceEl)
            ? sourceEl.GetString()
            : null;

        return new EvidenceReference(
            supportingFindingIds,
            supportingFiles,
            string.IsNullOrWhiteSpace(evidenceResolutionState) ? EvidenceReference.MissingState : evidenceResolutionState,
            string.IsNullOrWhiteSpace(evidenceSource) ? "synthesis_payload" : evidenceSource);
    }

    private static List<CandidateReviewFinding> BuildPerFileCandidateFindings(
        IReadOnlyList<ReviewFileResult> freshResults,
        IReadOnlyList<ReviewComment>? commentsOverride = null,
        IReviewClaimExtractor? reviewClaimExtractor = null)
    {
        var originalFindings = new List<CandidateReviewFinding>();
        var findingsBySignature = new Dictionary<string, Queue<CandidateReviewFinding>>(StringComparer.Ordinal);

        foreach (var fileResult in freshResults.Where(result => result.IsComplete && result.Comments is not null))
        {
            var comments = fileResult.Comments!;

            for (var index = 0; index < comments.Count; index++)
            {
                var comment = comments[index];
                var normalizedLineNumber = NormalizeLineNumber(comment.LineNumber);
                var finding = new CandidateReviewFinding(
                    BuildPerFileFindingId(fileResult, index + 1),
                    new CandidateFindingProvenance(
                        CandidateFindingProvenance.PerFileCommentOrigin,
                        "per_file_review",
                        fileResult.FilePath,
                        fileResult.Id,
                        index + 1),
                    comment.Severity,
                    comment.Message,
                    DetermineCategory(comment),
                    comment.FilePath,
                    normalizedLineNumber,
                    invariantCheckContext: BuildInvariantCheckContext(reviewClaimExtractor, fileResult, comment, index + 1));
                originalFindings.Add(finding);

                var signature = CreateCommentSignature(comment);
                if (!findingsBySignature.TryGetValue(signature, out var queue))
                {
                    queue = new Queue<CandidateReviewFinding>();
                    findingsBySignature[signature] = queue;
                }

                queue.Enqueue(finding);
            }
        }

        if (commentsOverride is null)
        {
            return originalFindings;
        }

        var finalFindings = new List<CandidateReviewFinding>(commentsOverride.Count);
        var derivedOrdinal = 1;
        foreach (var comment in commentsOverride)
        {
            var signature = CreateCommentSignature(comment);
            if (findingsBySignature.TryGetValue(signature, out var queue) && queue.Count > 0)
            {
                finalFindings.Add(queue.Dequeue());
                continue;
            }

            finalFindings.Add(CreateDerivedCandidateFinding(comment, derivedOrdinal++, reviewClaimExtractor));
        }

        return finalFindings;
    }

    private static string CreateCommentSignature(ReviewComment comment)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{comment.FilePath}|{NormalizeLineNumber(comment.LineNumber)}|{comment.Severity}|{comment.Message}");
    }

    private static CandidateReviewFinding CreateDerivedCandidateFinding(
        ReviewComment comment,
        int ordinal,
        IReviewClaimExtractor? reviewClaimExtractor)
    {
        if (TryBuildDerivedCrossFileEvidence(comment, out var evidence))
        {
            var findingId = $"finding-dedup-{ordinal:D3}";
            return new CandidateReviewFinding(
                findingId,
                new CandidateFindingProvenance(
                    CandidateFindingProvenance.PerFileCommentOrigin,
                    "finding_deduplication"),
                comment.Severity,
                comment.Message,
                CandidateReviewFinding.CrossCuttingCategory,
                null,
                null,
                evidence,
                "Cross-file concern derived from multiple per-file findings.",
                BuildInvariantCheckContext(reviewClaimExtractor, findingId, comment, CandidateReviewFinding.CrossCuttingCategory, null, null, evidence));
        }

        var derivedFindingId = $"finding-derived-{ordinal:D3}";
        var normalizedLineNumber = NormalizeLineNumber(comment.LineNumber);
        return new CandidateReviewFinding(
            derivedFindingId,
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                comment.FilePath is null ? "post_processing" : "quality_filter",
                comment.FilePath),
            comment.Severity,
            comment.Message,
            DetermineCategory(comment),
            comment.FilePath,
            normalizedLineNumber,
            invariantCheckContext: BuildInvariantCheckContext(
                reviewClaimExtractor,
                derivedFindingId,
                comment,
                DetermineCategory(comment),
                comment.FilePath,
                normalizedLineNumber,
                null));
    }

    private static IReadOnlyDictionary<string, string>? BuildInvariantCheckContext(
        IReviewClaimExtractor? reviewClaimExtractor,
        string findingId,
        ReviewComment comment,
        string category,
        string? filePath,
        int? lineNumber,
        EvidenceReference? evidence)
    {
        if (reviewClaimExtractor is null)
        {
            return null;
        }

        var normalizedLineNumber = NormalizeLineNumber(lineNumber);

        var probeFinding = new CandidateReviewFinding(
            findingId,
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "routing_probe",
                filePath),
            comment.Severity,
            comment.Message,
            category,
            filePath,
            normalizedLineNumber,
            evidence);

        var claims = reviewClaimExtractor.ExtractClaims(probeFinding);
        return claims.Count == 0 ? null : CandidateReviewFinding.CreateInvariantCheckContext(claims);
    }

    private static IReadOnlyDictionary<string, string>? BuildInvariantCheckContext(
        IReviewClaimExtractor? reviewClaimExtractor,
        ReviewFileResult fileResult,
        ReviewComment comment,
        int ordinal)
    {
        if (reviewClaimExtractor is null)
        {
            return null;
        }

        var normalizedLineNumber = NormalizeLineNumber(comment.LineNumber);

        var probeFinding = new CandidateReviewFinding(
            BuildPerFileFindingId(fileResult, ordinal),
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "per_file_review",
                fileResult.FilePath,
                fileResult.Id,
                ordinal),
            comment.Severity,
            comment.Message,
            DetermineCategory(comment),
            comment.FilePath,
            normalizedLineNumber);

        var claims = reviewClaimExtractor.ExtractClaims(probeFinding);
        return claims.Count == 0 ? null : CandidateReviewFinding.CreateInvariantCheckContext(claims);
    }

    private static bool TryBuildDerivedCrossFileEvidence(ReviewComment comment, out EvidenceReference? evidence)
    {
        evidence = null;
        if (comment.FilePath is not null || !comment.Message.StartsWith("[Cross-file]", StringComparison.Ordinal))
        {
            return false;
        }

        const string affectedFilesMarker = "Affected files:";
        var markerIndex = comment.Message.IndexOf(affectedFilesMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var filesText = comment.Message[(markerIndex + affectedFilesMarker.Length)..];
        var files = filesText
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length < 2)
        {
            return false;
        }

        evidence = new EvidenceReference([], files, EvidenceReference.ResolvedState, "derived_from_per_file_findings");
        return true;
    }

    private static IReadOnlyList<CandidateReviewFinding> AssignSynthesisFindingIds(IReadOnlyList<CandidateReviewFinding> synthesizedFindings)
    {
        if (synthesizedFindings.Count == 0)
        {
            return [];
        }

        var assigned = new List<CandidateReviewFinding>(synthesizedFindings.Count);
        for (var index = 0; index < synthesizedFindings.Count; index++)
        {
            var finding = synthesizedFindings[index];
            assigned.Add(
                new CandidateReviewFinding(
                    $"finding-cc-{index + 1:D3}",
                    finding.Provenance,
                    finding.Severity,
                    finding.Message,
                    finding.Category,
                    finding.FilePath,
                    finding.LineNumber,
                    finding.Evidence,
                    finding.CandidateSummaryText,
                    finding.InvariantCheckContext,
                    finding.VerificationOutcome));
        }

        return assigned;
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

    private static IReadOnlyList<ReviewComment> MaterializePublishedComments(
        IReadOnlyList<CandidateReviewFinding> candidateFindings,
        IReadOnlyList<FinalGateDecision> decisions)
    {
        var decisionsById = decisions.ToDictionary(decision => decision.FindingId, StringComparer.Ordinal);
        return candidateFindings
            .Where(finding => decisionsById.TryGetValue(finding.FindingId, out var decision)
                              && string.Equals(decision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal))
            .Select(finding => CreateReviewComment(finding.FilePath, finding.LineNumber, finding.Severity, finding.Message))
            .ToList();
    }

    internal static ReviewComment CreateReviewComment(string? filePath, int? lineNumber, CommentSeverity severity, string message)
    {
        return new ReviewComment(filePath, NormalizeLineNumber(lineNumber), severity, message);
    }

    internal static int? NormalizeLineNumber(int? lineNumber)
    {
        return lineNumber is > 0 ? lineNumber : null;
    }

    private static ReviewComment NormalizeCommentAnchor(ReviewComment comment)
    {
        var normalizedLineNumber = NormalizeLineNumber(comment.LineNumber);
        return normalizedLineNumber == comment.LineNumber
            ? comment
            : CreateReviewComment(comment.FilePath, normalizedLineNumber, comment.Severity, comment.Message);
    }

    private async Task RecordFinalGateProtocolAsync(
        Guid protocolId,
        IReadOnlyList<CandidateReviewFinding> findings,
        IReadOnlyList<FinalGateDecision> decisions,
        SummaryReconciliationResult reconciliation,
        CancellationToken ct)
    {
        var summary = RecordedFinalGateSummary.FromFindingsAndDecisions(findings, decisions, reconciliation);
        var summaryJson = JsonSerializer.Serialize(summary, FinalGateJsonOptions);
        var includedInFinalSummary = reconciliation.SummaryOnlyFindingIds.ToHashSet(StringComparer.Ordinal);

        await protocolRecorder.RecordReviewFindingGateEventAsync(
            protocolId,
            ReviewProtocolEventNames.ReviewFindingGateSummary,
            summaryJson,
            summaryJson,
            null,
            ct);

        var findingsById = findings.ToDictionary(finding => finding.FindingId, StringComparer.Ordinal);
        foreach (var decision in decisions)
        {
            if (!findingsById.TryGetValue(decision.FindingId, out var finding))
            {
                continue;
            }

            var recordedDecision = decision.ToRecordedDecision(
                finding,
                includedInFinalSummary.Contains(decision.FindingId));
            var details = JsonSerializer.Serialize(
                new
                {
                    decision.FindingId,
                    decision.Disposition,
                    decision.RuleSource,
                    decision.ReasonCodes,
                },
                FinalGateJsonOptions);
            var output = JsonSerializer.Serialize(recordedDecision, FinalGateJsonOptions);
            await protocolRecorder.RecordReviewFindingGateEventAsync(
                protocolId,
                ReviewProtocolEventNames.ReviewFindingGateDecision,
                details,
                output,
                null,
                ct);
        }
    }


    private async Task<IReadOnlyList<CandidateReviewFinding>> VerifyPrLevelFindingsAsync(
        IReadOnlyList<CandidateReviewFinding> synthesizedFindings,
        ReviewSystemContext baseContext,
        string sourceBranch,
        Guid? protocolId,
        CancellationToken ct)
    {
        if (synthesizedFindings.Count == 0 || reviewClaimExtractor is null || reviewEvidenceCollector is null)
        {
            return synthesizedFindings;
        }

        var prVerificationClient = baseContext.DefaultReviewChatClient ?? baseContext.TierChatClient ?? chatClient;
        var prVerificationModelId = baseContext.DefaultReviewModelId ?? baseContext.ModelId ?? this._opts.ModelId;
        var verified = new List<CandidateReviewFinding>(synthesizedFindings.Count);

        foreach (var finding in synthesizedFindings)
        {
            IReadOnlyList<ClaimDescriptor> claims;
            try
            {
                claims = reviewClaimExtractor.ExtractClaims(finding);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (protocolId.HasValue)
                {
                    await protocolRecorder.RecordVerificationEventAsync(
                        protocolId.Value,
                        ReviewProtocolEventNames.VerificationDegraded,
                        JsonSerializer.Serialize(
                            new
                            {
                                findingId = finding.FindingId,
                                stage = ClaimDescriptor.PrLevelStage,
                                degradedComponent = "claim_extraction",
                            }),
                        null,
                        ex.Message,
                        ct);
                }

                verified.Add(
                    new CandidateReviewFinding(
                        finding.FindingId,
                        finding.Provenance,
                        finding.Severity,
                        finding.Message,
                        finding.Category,
                        finding.FilePath,
                        finding.LineNumber,
                        finding.Evidence,
                        finding.CandidateSummaryText,
                        finding.InvariantCheckContext,
                        VerificationOutcome.DegradedUnresolved(
                            finding.FindingId,
                            VerificationOutcome.DeterministicRulesEvaluator,
                            ReviewFindingGateReasonCodes.VerificationDegraded,
                            $"PR-level claim extraction degraded: {ex.Message}")));
                continue;
            }

            if (claims.Count == 0)
            {
                verified.Add(finding);
                continue;
            }

            var claim = claims[0];

            var workItem = new VerificationWorkItem(
                claim,
                finding.Provenance,
                claim.Stage,
                VerificationWorkItem.CrossFileScope,
                true,
                finding.Evidence);

            EvidenceBundle evidence;
            try
            {
                evidence = await reviewEvidenceCollector.CollectEvidenceAsync(workItem, baseContext.ReviewTools, sourceBranch, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (protocolId.HasValue)
                {
                    await protocolRecorder.RecordVerificationEventAsync(
                        protocolId.Value,
                        ReviewProtocolEventNames.VerificationDegraded,
                        JsonSerializer.Serialize(
                            new
                            {
                                findingId = finding.FindingId,
                                claimId = claim.ClaimId,
                                stage = ClaimDescriptor.PrLevelStage,
                                degradedComponent = "evidence_collection",
                            }),
                        null,
                        ex.Message,
                        ct);
                }

                verified.Add(
                    new CandidateReviewFinding(
                        finding.FindingId,
                        finding.Provenance,
                        finding.Severity,
                        finding.Message,
                        finding.Category,
                        finding.FilePath,
                        finding.LineNumber,
                        finding.Evidence,
                        finding.CandidateSummaryText,
                        finding.InvariantCheckContext,
                        VerificationOutcome.DegradedUnresolved(
                            claim,
                            VerificationOutcome.AiMicroVerifierEvaluator,
                            ReviewFindingGateReasonCodes.VerificationDegraded,
                            $"PR-level evidence collection degraded: {ex.Message}")));
                continue;
            }

            if (protocolId.HasValue)
            {
                await protocolRecorder.RecordVerificationEventAsync(
                    protocolId.Value,
                    ReviewProtocolEventNames.VerificationEvidenceCollected,
                    JsonSerializer.Serialize(
                        new
                        {
                            findingId = finding.FindingId,
                            claimId = claim.ClaimId,
                            coverageState = evidence.CoverageState,
                        }),
                    JsonSerializer.Serialize(evidence, FinalGateJsonOptions),
                    null,
                    ct);
            }

            var supportingFiles = evidence.EvidenceItems
                .Select(item => item.SourceId)
                .Where(sourceId => !string.IsNullOrWhiteSpace(sourceId))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var evidenceState = evidence.CoverageState switch
            {
                EvidenceBundle.CompleteCoverage => EvidenceReference.ResolvedState,
                EvidenceBundle.PartialCoverage => EvidenceReference.PartialState,
                _ => EvidenceReference.MissingState,
            };

            var updatedEvidence = finding.Evidence is null
                ? new EvidenceReference([], supportingFiles, evidenceState, "review_context_tools")
                : new EvidenceReference(
                    finding.Evidence.SupportingFindingIds,
                    supportingFiles.Length > 0 ? supportingFiles : finding.Evidence.SupportingFiles,
                    evidenceState,
                    finding.Evidence.EvidenceSource);

            var evidenceBackedWorkItem = new VerificationWorkItem(
                claim,
                finding.Provenance,
                claim.Stage,
                VerificationWorkItem.CrossFileScope,
                true,
                updatedEvidence);

            VerificationOutcome outcome;
            if (prVerificationClient is null)
            {
                outcome = new VerificationOutcome(
                    claim.ClaimId,
                    claim.FindingId,
                    VerificationOutcome.UnresolvedKind,
                    FinalGateDecision.SummaryOnlyDisposition,
                    [updatedEvidence.HasResolvedMultiFileEvidence
                        ? ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport
                        : ReviewFindingGateReasonCodes.MissingMultiFileEvidence],
                    [],
                    VerificationOutcome.WeakEvidence,
                    "Retrieved context is treated as a verification hint until a bounded claim outcome supports publication.",
                    VerificationOutcome.AiMicroVerifierEvaluator,
                    false);
            }
            else
            {
                var systemPrompt = ReviewPrompts.BuildPrVerificationSystemPrompt(baseContext);
                var userMessage = ReviewPrompts.BuildPrVerificationUserMessage(claim, evidence);

                try
                {
                    var response = await prVerificationClient.GetResponseAsync(
                        [
                            new ChatMessage(ChatRole.System, systemPrompt),
                            new ChatMessage(ChatRole.User, userMessage),
                        ],
                        new ChatOptions { ModelId = prVerificationModelId, Temperature = baseContext.Temperature },
                        ct);

                    var responseText = response.Text ?? string.Empty;
                    if (protocolId.HasValue)
                    {
                        await protocolRecorder.RecordAiCallAsync(
                            protocolId.Value,
                            0,
                            response.Usage?.InputTokenCount,
                            response.Usage?.OutputTokenCount,
                            userMessage,
                            systemPrompt,
                            responseText,
                            ct,
                            "ai_call_pr_verification");
                        await protocolRecorder.AddTokensAsync(
                            protocolId.Value,
                            response.Usage?.InputTokenCount ?? 0,
                            response.Usage?.OutputTokenCount ?? 0,
                            AiConnectionModelCategory.Default,
                            prVerificationModelId,
                            ct);
                    }

                    if (!TryParsePrVerificationResponse(responseText, claim, out outcome))
                    {
                        if (protocolId.HasValue)
                        {
                            await protocolRecorder.RecordVerificationEventAsync(
                                protocolId.Value,
                                ReviewProtocolEventNames.VerificationDegraded,
                                JsonSerializer.Serialize(
                                    new
                                    {
                                        findingId = finding.FindingId,
                                        claimId = claim.ClaimId,
                                        stage = ClaimDescriptor.PrLevelStage,
                                        degradedComponent = "bounded_ai_response_parse",
                                    }),
                                responseText,
                                "PR-level verification response could not be parsed.",
                                ct);
                        }

                        outcome = VerificationOutcome.DegradedUnresolved(
                            claim,
                            VerificationOutcome.AiMicroVerifierEvaluator,
                            ReviewFindingGateReasonCodes.VerificationDegraded,
                            "AI micro-verification degraded: response could not be parsed.");
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (protocolId.HasValue)
                    {
                        await protocolRecorder.RecordVerificationEventAsync(
                            protocolId.Value,
                            ReviewProtocolEventNames.VerificationDegraded,
                            JsonSerializer.Serialize(
                                new
                                {
                                    findingId = finding.FindingId,
                                    claimId = claim.ClaimId,
                                    stage = ClaimDescriptor.PrLevelStage,
                                    degradedComponent = "bounded_ai_verification",
                                }),
                            null,
                            ex.Message,
                            ct);
                    }

                    outcome = VerificationOutcome.DegradedUnresolved(
                        claim,
                        VerificationOutcome.AiMicroVerifierEvaluator,
                        ReviewFindingGateReasonCodes.VerificationDegraded,
                        $"AI micro-verification degraded: {ex.Message}");
                }
            }

            if (protocolId.HasValue)
            {
                await protocolRecorder.RecordVerificationEventAsync(
                    protocolId.Value,
                    ReviewProtocolEventNames.VerificationPrDecision,
                    JsonSerializer.Serialize(
                        new
                        {
                            findingId = outcome.FindingId,
                            claimId = outcome.ClaimId,
                            verifierFamilies = evidenceBackedWorkItem.VerifierFamilies,
                        }),
                    JsonSerializer.Serialize(outcome, FinalGateJsonOptions),
                    null,
                    ct);
            }

            verified.Add(
                new CandidateReviewFinding(
                    finding.FindingId,
                    finding.Provenance,
                    finding.Severity,
                    finding.Message,
                    finding.Category,
                    finding.FilePath,
                    finding.LineNumber,
                    evidenceBackedWorkItem.ExistingEvidence,
                    finding.CandidateSummaryText,
                    finding.InvariantCheckContext,
                    outcome));
        }

        return verified;
    }

    private static bool LooksLikeJsonObject(string text)
    {
        return StripMarkdownCodeFences(text).StartsWith("{", StringComparison.Ordinal);
    }

    private static string StripMarkdownCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline >= 0)
        {
            trimmed = trimmed[(firstNewline + 1)..];
        }
        else
        {
            var braceStart = trimmed.IndexOf('{');
            if (braceStart >= 0)
            {
                trimmed = trimmed[braceStart..];
            }
        }

        var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence >= 0)
        {
            trimmed = trimmed[..closingFence];
        }

        return trimmed.Trim();
    }

    /// <summary>
    ///     Parses the JSON response from the cross-file quality-filter AI call.
    ///     Returns an empty list on any parse failure (fallback: keep original comments).
    /// </summary>
    internal static List<ReviewComment> ParseQualityFilterResponse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (!doc.RootElement.TryGetProperty("comments", out var commentsEl) ||
                commentsEl.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<ReviewComment>();
            foreach (var item in commentsEl.EnumerateArray())
            {
                var message = item.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(message))
                {
                    continue;
                }

                string? filePath = null;
                if (item.TryGetProperty("file_path", out var fpEl) && fpEl.ValueKind == JsonValueKind.String)
                {
                    filePath = fpEl.GetString();
                }

                int? lineNumber = null;
                if (item.TryGetProperty("line_number", out var lnEl) && lnEl.ValueKind == JsonValueKind.Number)
                {
                    lineNumber = NormalizeLineNumber(lnEl.GetInt32());
                }

                var severity = CommentSeverity.Warning;
                if (item.TryGetProperty("severity", out var sevEl))
                {
                    severity = sevEl.GetString()?.ToLowerInvariant() switch
                    {
                        "error" => CommentSeverity.Error,
                        "suggestion" => CommentSeverity.Suggestion,
                        "info" => CommentSeverity.Info,
                        _ => CommentSeverity.Warning,
                    };
                }

                result.Add(CreateReviewComment(filePath, lineNumber, severity, message));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
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
        try
        {
            LogQualityFilterStarted(logger, jobId, comments.Count);

            var systemPrompt = ReviewPrompts.BuildQualityFilterSystemPrompt(baseContext);
            var userMessage = ReviewPrompts.BuildQualityFilterUserMessage(comments);

            var response = await effectiveClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userMessage),
                ],
                new ChatOptions { ModelId = baseContext.ModelId ?? this._opts.ModelId, Temperature = baseContext.Temperature },
                ct);

            var parsed = ParseQualityFilterResponse(response.Text ?? string.Empty);
            var kept = parsed.Count > 0 ? parsed : comments;

            LogQualityFilterCompleted(logger, jobId, comments.Count, kept.Count);
            return kept;
        }
        catch (Exception ex)
        {
            LogQualityFilterFailed(logger, jobId, ex);
            return comments;
        }
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
