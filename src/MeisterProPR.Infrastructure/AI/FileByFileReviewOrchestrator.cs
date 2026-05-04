// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
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
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AI;

public sealed partial class FileByFileReviewOrchestrator(
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
    ISummaryReconciliationService? summaryReconciliationService = null) : IFileByFileReviewOrchestrator
{
    private static readonly JsonSerializerOptions CommentRelevanceJsonOptions = new(JsonSerializerDefaults.Web);
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

        // US2: Exclude files matching repository exclusion rules before parallel dispatch.
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
                    await this.ReviewSingleFileAsync(
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

    private async Task ReviewSingleFileAsync(
        ReviewJob job,
        PullRequest pr,
        ChangedFile file,
        int fileIndex,
        int totalFiles,
        ReviewSystemContext baseContext,
        ReviewFileResult? existingResult,
        IChatClient effectiveClient,
        CancellationToken ct)
    {
        LogFileReviewStarted(logger, file.Path, fileIndex, totalFiles, job.Id);

        ReviewFileResult fileResult;
        if (existingResult is { IsComplete: false })
        {
            // Reuse the existing row — covers both jobs killed mid-flight (interrupted)
            // and previously failed rows (IsFailed=true, IsComplete=false).
            // Reset it so MarkCompleted / MarkFailed work correctly.
            existingResult.ResetForRetry();
            await jobRepository.UpdateFileResultAsync(existingResult, ct);
            fileResult = existingResult;
        }
        else
        {
            fileResult = new ReviewFileResult(job.Id, file.Path);
            await jobRepository.AddFileResultAsync(fileResult, ct);
        }

        // Classify file complexity early so we can record tier in the protocol
        var tier = ClassifyTier(file);
        var tierCategory = tier switch
        {
            FileComplexityTier.Low => AiConnectionModelCategory.LowEffort,
            FileComplexityTier.Medium => AiConnectionModelCategory.MediumEffort,
            FileComplexityTier.High => AiConnectionModelCategory.HighEffort,
            _ => AiConnectionModelCategory.MediumEffort,
        };

        var tierPurpose = tier switch
        {
            FileComplexityTier.Low => AiPurpose.ReviewLowEffort,
            FileComplexityTier.Medium => AiPurpose.ReviewMediumEffort,
            FileComplexityTier.High => AiPurpose.ReviewHighEffort,
            _ => AiPurpose.ReviewMediumEffort,
        };

        // Resolve tier-specific IChatClient if configured
        IChatClient? tierClient = null;
        AiConnectionDto? tierDto = null;
        string? tierModelId = null;
        if (aiRuntimeResolver is not null)
        {
            try
            {
                var tierRuntime = await aiRuntimeResolver.ResolveChatRuntimeAsync(job.ClientId, tierPurpose, ct);
                tierDto = tierRuntime.Connection;
                tierClient = tierRuntime.ChatClient;
                tierModelId = tierRuntime.Model.RemoteModelId;
            }
            catch
            {
                tierDto = null;
                tierClient = null;
                tierModelId = null;
            }
        }
        else if (aiConnectionRepository is not null && aiClientFactory is not null)
        {
            tierDto = await aiConnectionRepository.GetForTierAsync(job.ClientId, tierCategory, ct);
            if (tierDto is not null)
            {
                tierClient = aiClientFactory.CreateClient(tierDto.BaseUrl, tierDto.Secret);
                tierModelId = tierDto.GetBoundModelId(tierPurpose)
                              ?? tierDto.ConfiguredModels.FirstOrDefault(model => model.SupportsChat)?.RemoteModelId;
            }
        }

        Guid? protocolId = null;
        try
        {
            protocolId = await protocolRecorder.BeginAsync(
                job.Id,
                job.RetryCount + 1,
                file.Path,
                fileResult.Id,
                tierCategory,
                tierModelId,
                ct);
        }
        catch (Exception ex)
        {
            LogProtocolBeginFailed(logger, file.Path, job.Id, ex);
        }

        ReviewSystemContext? fileContext = null;
        try
        {
            // Filter threads for this file
            var relevantThreads = FilterThreadsForFile(pr.ExistingThreads, file.Path);

            var filePr = new PullRequest(
                pr.OrganizationUrl,
                pr.ProjectId,
                pr.RepositoryId,
                pr.RepositoryName,
                pr.PullRequestId,
                pr.IterationId,
                pr.Title,
                pr.Description,
                pr.SourceBranch,
                pr.TargetBranch,
                [file],
                pr.Status,
                relevantThreads);

            var changedLinesCount = CountChangedLines(file.UnifiedDiff);

            LogTierAssigned(logger, file.Path, tier, changedLinesCount, job.Id);

            var maxIterationsOverride = tier switch
            {
                FileComplexityTier.Low => this._opts.MaxIterationsLow,
                FileComplexityTier.Medium => this._opts.MaxIterationsMedium,
                FileComplexityTier.High => this._opts.MaxIterationsHigh,
                _ => this._opts.MaxIterations,
            };

            if (maxIterationsOverride != this._opts.MaxIterations)
            {
                LogMaxIterationsOverrideApplied(logger, maxIterationsOverride, file.Path, job.Id);
            }

            fileContext = new ReviewSystemContext(
                baseContext.ClientSystemMessage,
                baseContext.RepositoryInstructions,
                baseContext.ReviewTools)
            {
                ActiveProtocolId = protocolId,
                DefaultReviewChatClient = baseContext.DefaultReviewChatClient,
                DefaultReviewModelId = baseContext.DefaultReviewModelId,
                ProtocolRecorder = protocolId.HasValue ? protocolRecorder : null,
                // Set per-file hint so ToolAwareAiReviewCore uses per-file prompts
                PerFileHint = new PerFileReviewHint(file.Path, fileIndex, totalFiles, pr.AllPrFileSummaries)
                {
                    ComplexityTier = tier,
                    MaxIterationsOverride = maxIterationsOverride,
                },
                ExclusionRules = baseContext.ExclusionRules,
                DismissedPatterns = baseContext.DismissedPatterns,
                ModelId = tierModelId ?? baseContext.ModelId,
                // Tier-specific client wins; fall back to per-client active connection so the
                // global default chatClient is never used when a per-client connection is configured.
                TierChatClient = tierClient ?? effectiveClient,
            };

            LogDismissalsInjected(logger, fileContext.DismissedPatterns?.Count ?? 0, file.Path, job.Id);

            // Custom prompts — driven by PerFileHint set above
            var result = await aiCore.ReviewAsync(filePr, fileContext, ct);

            // Apply confidence-gated severity floor first, so that downgraded
            // severities are visible to the SUGGESTION-specific vague-phrase filter below.
            var commentsBeforeConfidenceFloor = result.Comments;
            result = ApplyConfidenceFloor(result, fileContext.LoopMetrics?.FinalConfidence, this._opts);
            var confidenceDroppedCount = CountSeverityDowngrades(commentsBeforeConfidenceFloor, result.Comments);
            if (confidenceDroppedCount > 0)
            {
                LogSeverityDowngraded(logger, confidenceDroppedCount, file.Path, job.Id);
            }

            // Discard speculative/hedge-phrase comments.
            var beforeHedge = result.Comments.Count;
            result = FilterSpeculativeComments(result);
            var hedgeDropped = beforeHedge - result.Comments.Count;
            if (hedgeDropped > 0)
            {
                LogSpeculativeCommentsDropped(logger, hedgeDropped, file.Path, job.Id);
            }

            // Strip INFO-severity comments — they belong in the summary only.
            var beforeInfo = result.Comments.Count;
            result = StripInfoComments(result);
            var infoDropped = beforeInfo - result.Comments.Count;
            if (infoDropped > 0)
            {
                LogInfoCommentsDropped(logger, infoDropped, file.Path, job.Id);
            }

            // Discard vague SUGGESTION comments that lack a concrete alternative.
            var beforeVague = result.Comments.Count;
            result = FilterVagueSuggestions(result);
            var vagueDropped = beforeVague - result.Comments.Count;
            if (vagueDropped > 0)
            {
                LogVagueSuggestionsDropped(logger, vagueDropped, file.Path, job.Id);
            }

            var preFilterComments = result.Comments;
            var filterResult = await this.ApplyCommentRelevanceFilterAsync(
                job,
                file,
                filePr,
                fileResult,
                fileContext,
                preFilterComments,
                protocolId,
                ct);

            if (filterResult is not null)
            {
                result = result with { Comments = filterResult.GetKeptComments() };
            }

            // Memory-augmented reconsideration — query historical thread embeddings and
            // reconsider findings in light of past resolutions. Falls through unchanged on any failure.
            if (memoryService is not null)
            {
                result = await memoryService.RetrieveAndReconsiderAsync(
                    job.ClientId,
                    job,
                    file.Path,
                    file.UnifiedDiff,
                    result,
                    protocolId,
                    ct,
                    fileContext.Temperature);
            }

            result = NormalizeCommentAnchors(result);

            result = await this.ApplyLocalVerificationAsync(
                result,
                fileResult,
                protocolId,
                reviewInvariantFactProviders?.SelectMany(provider => provider.GetFacts()).ToList() ?? [],
                ct);

            fileResult.MarkCompleted(result.Summary, result.Comments);
            await jobRepository.UpdateFileResultAsync(fileResult, ct);

            if (protocolId.HasValue && fileContext.LoopMetrics is not null)
            {
                var m = fileContext.LoopMetrics;
                await protocolRecorder.SetCompletedAsync(
                    protocolId.Value,
                    "Completed",
                    m.TotalInputTokens,
                    m.TotalOutputTokens,
                    m.Iterations,
                    m.ToolCallCount,
                    m.FinalConfidence,
                    ct);
            }

            LogFileReviewCompleted(logger, file.Path, job.Id);
        }
        catch (Exception ex)
        {
            fileResult.MarkFailed(ex.Message);
            await jobRepository.UpdateFileResultAsync(fileResult, ct);

            if (protocolId.HasValue)
            {
                var m = fileContext?.LoopMetrics;
                await protocolRecorder.RecordAiCallAsync(
                    protocolId.Value,
                    m?.Iterations ?? 1,
                    0,
                    0,
                    null,
                    null,
                    null,
                    ct,
                    "ai_call_failure",
                    ex.Message);

                await protocolRecorder.SetCompletedAsync(
                    protocolId.Value,
                    "Failed",
                    m?.TotalInputTokens ?? 0,
                    m?.TotalOutputTokens ?? 0,
                    m?.Iterations ?? 0,
                    m?.ToolCallCount ?? 0,
                    null,
                    ct);
            }

            throw;
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

    private async Task<CommentRelevanceFilterResult?> ApplyCommentRelevanceFilterAsync(
        ReviewJob job,
        ChangedFile file,
        PullRequest filePr,
        ReviewFileResult fileResult,
        ReviewSystemContext fileContext,
        IReadOnlyList<ReviewComment> comments,
        Guid? protocolId,
        CancellationToken ct)
    {
        if (commentRelevanceFilterRegistry is null || !commentRelevanceFilterRegistry.HasSelection)
        {
            return null;
        }

        var request = new CommentRelevanceFilterRequest(
            job.Id,
            fileResult.Id,
            commentRelevanceFilterRegistry.Selection.SelectedImplementationId,
            file.Path,
            file,
            filePr,
            comments,
            fileContext,
            protocolId);

        if (!commentRelevanceFilterRegistry.TryResolveSelected(out var selectedFilter) || selectedFilter is null)
        {
            var fallback = BuildSelectionFallbackResult(request, comments);
            await this.RecordCommentRelevanceProtocolAsync(protocolId, fallback, ReviewProtocolEventNames.CommentRelevanceFilterSelectionFallback, ct);
            return fallback;
        }

        try
        {
            var result = await selectedFilter.FilterAsync(request, ct);
            var eventName = result.DegradedComponents.Contains("comment_relevance_evaluator", StringComparer.Ordinal)
                ? ReviewProtocolEventNames.CommentRelevanceEvaluatorDegraded
                : result.DegradedComponents.Count > 0
                    ? ReviewProtocolEventNames.CommentRelevanceFilterDegraded
                    : ReviewProtocolEventNames.CommentRelevanceFilterOutput;
            await this.RecordCommentRelevanceProtocolAsync(protocolId, result, eventName, ct);
            await this.RecordCommentRelevanceAiUsageAsync(protocolId, result, ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var fallback = BuildFailureFallbackResult(request, comments, ex.Message);
            await this.RecordCommentRelevanceProtocolAsync(protocolId, fallback, ReviewProtocolEventNames.CommentRelevanceFilterDegraded, ct);
            return fallback;
        }
    }

    private static CommentRelevanceFilterResult BuildSelectionFallbackResult(
        CommentRelevanceFilterRequest request,
        IReadOnlyList<ReviewComment> comments)
    {
        return BuildFallbackResult(
            request,
            comments,
            "comment_relevance_registry",
            "pre_filter_comments_retained",
            $"Selected comment relevance filter '{request.SelectedImplementationId}' was not registered.");
    }

    private static CommentRelevanceFilterResult BuildFailureFallbackResult(
        CommentRelevanceFilterRequest request,
        IReadOnlyList<ReviewComment> comments,
        string? error)
    {
        return BuildFallbackResult(
            request,
            comments,
            request.SelectedImplementationId ?? "comment_relevance_filter",
            "pre_filter_comments_retained",
            string.IsNullOrWhiteSpace(error)
                ? "Comment relevance filter failed; pre-filter comments were retained unchanged."
                : $"Comment relevance filter failed; pre-filter comments were retained unchanged. Cause: {error}");
    }

    private static CommentRelevanceFilterResult BuildFallbackResult(
        CommentRelevanceFilterRequest request,
        IReadOnlyList<ReviewComment> comments,
        string degradedComponent,
        string fallbackCheck,
        string degradedCause)
    {
        var decisions = comments
            .Select(comment => new CommentRelevanceFilterDecision(
                CommentRelevanceFilterDecision.KeepDecision,
                comment,
                [],
                CommentRelevanceFilterDecision.FallbackModeSource))
            .ToList()
            .AsReadOnly();

        return new CommentRelevanceFilterResult(
            request.SelectedImplementationId ?? "unknown",
            "fallback",
            request.FilePath,
            comments.Count,
            decisions,
            [degradedComponent],
            [fallbackCheck],
            degradedCause);
    }

    private async Task RecordCommentRelevanceProtocolAsync(
        Guid? protocolId,
        CommentRelevanceFilterResult result,
        string eventName,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        var output = result.ToRecordedOutput();
        var details = JsonSerializer.Serialize(
            new
            {
                implementationId = result.ImplementationId,
                implementationVersion = result.ImplementationVersion,
                filePath = result.FilePath,
                originalCommentCount = result.OriginalCommentCount,
                keptCount = result.KeptCount,
                discardedCount = result.DiscardedCount,
                degradedComponents = result.DegradedComponents,
                fallbackChecks = result.FallbackChecks,
                degradedCause = result.DegradedCause,
            });

        await protocolRecorder.RecordCommentRelevanceEventAsync(
            protocolId.Value,
            eventName,
            details,
            JsonSerializer.Serialize(output, CommentRelevanceJsonOptions),
            null,
            ct);
    }

    private async Task RecordCommentRelevanceAiUsageAsync(
        Guid? protocolId,
        CommentRelevanceFilterResult result,
        CancellationToken ct)
    {
        if (!protocolId.HasValue || result.AiTokenUsage is null)
        {
            return;
        }

        await protocolRecorder.RecordAiCallAsync(
            protocolId.Value,
            0,
            result.AiTokenUsage.InputTokens,
            result.AiTokenUsage.OutputTokens,
            JsonSerializer.Serialize(new { filePath = result.FilePath, implementationId = result.ImplementationId }),
            JsonSerializer.Serialize(new { implementationId = result.ImplementationId, result = "completed" }),
            null,
            ct,
            ReviewProtocolEventNames.CommentRelevanceEvaluatorAiCall);

        await protocolRecorder.AddTokensAsync(
            protocolId.Value,
            result.AiTokenUsage.InputTokens,
            result.AiTokenUsage.OutputTokens,
            result.AiTokenUsage.ModelCategory,
            result.AiTokenUsage.ModelId,
            ct);
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

    private static string BuildPerFileFindingId(ReviewFileResult fileResult, int ordinal)
    {
        return $"finding-pf-{fileResult.Id:N}-{ordinal:D3}";
    }

    private static string DetermineCategory(ReviewComment comment)
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

    private static ReviewResult NormalizeCommentAnchors(ReviewResult result)
    {
        var normalizedComments = NormalizeCommentAnchors(result.Comments);
        return ReferenceEquals(normalizedComments, result.Comments)
            ? result
            : result with { Comments = normalizedComments };
    }

    private static IReadOnlyList<ReviewComment> NormalizeCommentAnchors(IReadOnlyList<ReviewComment> comments)
    {
        List<ReviewComment>? normalizedComments = null;

        for (var index = 0; index < comments.Count; index++)
        {
            var comment = comments[index];
            var normalizedComment = NormalizeCommentAnchor(comment);

            if (normalizedComments is null)
            {
                if (ReferenceEquals(normalizedComment, comment))
                {
                    continue;
                }

                normalizedComments = new List<ReviewComment>(comments.Count);
                for (var preservedIndex = 0; preservedIndex < index; preservedIndex++)
                {
                    normalizedComments.Add(comments[preservedIndex]);
                }
            }

            normalizedComments.Add(normalizedComment);
        }

        return normalizedComments is null ? comments : normalizedComments.AsReadOnly();
    }

    private static ReviewComment NormalizeCommentAnchor(ReviewComment comment)
    {
        var normalizedLineNumber = NormalizeLineNumber(comment.LineNumber);
        return normalizedLineNumber == comment.LineNumber
            ? comment
            : CreateReviewComment(comment.FilePath, normalizedLineNumber, comment.Severity, comment.Message);
    }

    private static ReviewComment CreateReviewComment(string? filePath, int? lineNumber, CommentSeverity severity, string message)
    {
        return new ReviewComment(filePath, NormalizeLineNumber(lineNumber), severity, message);
    }

    private static int? NormalizeLineNumber(int? lineNumber)
    {
        return lineNumber is > 0 ? lineNumber : null;
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

    private async Task<ReviewResult> ApplyLocalVerificationAsync(
        ReviewResult result,
        ReviewFileResult fileResult,
        Guid? protocolId,
        IReadOnlyList<InvariantFact> invariantFacts,
        CancellationToken ct)
    {
        if (reviewClaimExtractor is null || reviewFindingVerifier is null || result.Comments.Count == 0)
        {
            return result;
        }

        var candidateFindings = result.Comments
            .Select((comment, index) => new CandidateReviewFinding(
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
                NormalizeLineNumber(comment.LineNumber)))
            .ToList();

        var claimsByFindingId = new Dictionary<string, IReadOnlyList<ClaimDescriptor>>(StringComparer.Ordinal);
        foreach (var finding in candidateFindings)
        {
            try
            {
                claimsByFindingId[finding.FindingId] = reviewClaimExtractor.ExtractClaims(finding);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                claimsByFindingId[finding.FindingId] = [];
                if (protocolId.HasValue)
                {
                    await protocolRecorder.RecordVerificationEventAsync(
                        protocolId.Value,
                        ReviewProtocolEventNames.VerificationDegraded,
                        JsonSerializer.Serialize(
                            new
                            {
                                findingId = finding.FindingId,
                                stage = ClaimDescriptor.LocalStage,
                                degradedComponent = "claim_extraction",
                            }),
                        null,
                        ex.Message,
                        ct);
                }
            }
        }

        var workItems = candidateFindings
            .SelectMany(finding => claimsByFindingId[finding.FindingId]
                .Select(claim => new VerificationWorkItem(
                    claim,
                    finding.Provenance,
                    claim.Stage,
                    VerificationWorkItem.AnchorOnlyScope,
                    false)))
            .ToList();

        if (protocolId.HasValue)
        {
            foreach (var finding in candidateFindings)
            {
                var claims = claimsByFindingId[finding.FindingId];
                if (claims.Count == 0)
                {
                    continue;
                }

                await protocolRecorder.RecordVerificationEventAsync(
                    protocolId.Value,
                    ReviewProtocolEventNames.VerificationClaimsExtracted,
                    JsonSerializer.Serialize(
                        new
                        {
                            findingId = finding.FindingId,
                            filePath = finding.FilePath,
                            claimCount = claims.Count,
                        }),
                    JsonSerializer.Serialize(claims, FinalGateJsonOptions),
                    null,
                    ct);
            }
        }

        if (workItems.Count == 0)
        {
            return result;
        }

        var outcomes = await reviewFindingVerifier.VerifyAsync(workItems, invariantFacts, ct);
        var outcomesByFindingId = outcomes
            .GroupBy(outcome => outcome.FindingId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<VerificationOutcome>)group.ToList(),
                StringComparer.Ordinal);

        if (protocolId.HasValue)
        {
            foreach (var outcome in outcomes)
            {
                await protocolRecorder.RecordVerificationEventAsync(
                    protocolId.Value,
                    ReviewProtocolEventNames.VerificationLocalDecision,
                    JsonSerializer.Serialize(
                        new
                        {
                            findingId = outcome.FindingId,
                            claimId = outcome.ClaimId,
                        }),
                    JsonSerializer.Serialize(outcome, FinalGateJsonOptions),
                    null,
                ct);
            }
        }

        var withheldFindingIds = outcomesByFindingId
            .Where(entry => !AreLocalOutcomesPublishable(entry.Value))
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (withheldFindingIds.Count == 0)
        {
            return result;
        }

        var verifiedFindings = candidateFindings
            .Where(finding => !outcomesByFindingId.TryGetValue(finding.FindingId, out var findingOutcomes) || AreLocalOutcomesPublishable(findingOutcomes))
            .ToList();
        var verifiedComments = verifiedFindings
            .Select(finding => CreateReviewComment(finding.FilePath, finding.LineNumber, finding.Severity, finding.Message))
            .ToList();
        var verifiedSummary = RewriteLocalVerificationSummary(candidateFindings, verifiedFindings, outcomesByFindingId);

        return result with
        {
            Summary = verifiedSummary,
            Comments = verifiedComments,
        };
    }

    private static bool AreLocalOutcomesPublishable(IReadOnlyList<VerificationOutcome> outcomes)
    {
        return outcomes.Count == 0 || outcomes.All(outcome =>
            string.Equals(outcome.RecommendedDisposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal));
    }

    private static string RewriteLocalVerificationSummary(
        IReadOnlyList<CandidateReviewFinding> originalFindings,
        IReadOnlyList<CandidateReviewFinding> verifiedFindings,
        IReadOnlyDictionary<string, IReadOnlyList<VerificationOutcome>> outcomesByFindingId)
    {
        var summaryOnlyCount = 0;
        var dropCount = 0;

        foreach (var finding in originalFindings)
        {
            if (!outcomesByFindingId.TryGetValue(finding.FindingId, out var outcomes) || AreLocalOutcomesPublishable(outcomes))
            {
                continue;
            }

            if (outcomes.Any(outcome => string.Equals(outcome.RecommendedDisposition, FinalGateDecision.DropDisposition, StringComparison.Ordinal)))
            {
                dropCount++;
                continue;
            }

            summaryOnlyCount++;
        }

        if (verifiedFindings.Count == 0)
        {
            var noFindingsBuilder = new StringBuilder("No actionable local findings remained after verification.");
            AppendLocalVerificationSuppressionSummary(noFindingsBuilder, summaryOnlyCount, dropCount);
            return noFindingsBuilder.ToString();
        }

        var builder = new StringBuilder();
        builder.Append($"Local verification retained {verifiedFindings.Count} actionable finding");
        builder.Append(verifiedFindings.Count == 1 ? "." : "s.");
        AppendLocalVerificationSuppressionSummary(builder, summaryOnlyCount, dropCount);

        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("Verified local findings:");

        foreach (var message in verifiedFindings
                     .Select(finding => finding.Message)
                     .Distinct(StringComparer.Ordinal)
                     .Take(5))
        {
            builder.Append("- ");
            builder.AppendLine(message);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendLocalVerificationSuppressionSummary(StringBuilder builder, int summaryOnlyCount, int dropCount)
    {
        if (summaryOnlyCount > 0)
        {
            builder.Append(' ');
            builder.Append(summaryOnlyCount);
            builder.Append(summaryOnlyCount == 1
                ? " candidate finding was withheld pending stronger evidence."
                : " candidate findings were withheld pending stronger evidence.");
        }

        if (dropCount > 0)
        {
            builder.Append(' ');
            builder.Append(dropCount);
            builder.Append(dropCount == 1
                ? " candidate finding was dropped by deterministic verification."
                : " candidate findings were dropped by deterministic verification.");
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

    private static IReadOnlyList<PrCommentThread> FilterThreadsForFile(
        IReadOnlyList<PrCommentThread>? allThreads,
        string filePath)
    {
        if (allThreads is null)
        {
            return [];
        }

        return allThreads.Where(t => t.FilePath == filePath || t.FilePath == null).ToList();
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Starting review for file {FilePath} ({Index}/{Total}) in job {JobId}")]
    private static partial void LogFileReviewStarted(ILogger logger, string filePath, int index, int total, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed review for file {FilePath} in job {JobId}")]
    private static partial void LogFileReviewCompleted(ILogger logger, string filePath, Guid jobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed review for file {FilePath} in job {JobId}")]
    private static partial void LogFileReviewFailed(ILogger logger, string filePath, Guid jobId, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting synthesis for job {JobId}")]
    private static partial void LogSynthesisStarted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed synthesis for job {JobId}")]
    private static partial void LogSynthesisCompleted(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Synthesis failed for job {JobId} — using fallback concatenation")]
    private static partial void LogSynthesisFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Synthesis for job {JobId} returned invalid JSON — requesting one repair pass")]
    private static partial void LogSynthesisJsonRepairStarted(ILogger logger, Guid jobId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Synthesis JSON repair succeeded for job {JobId}")]
    private static partial void LogSynthesisJsonRepairSucceeded(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Synthesis JSON repair failed for job {JobId} — using fallback concatenation")]
    private static partial void LogSynthesisJsonRepairFailed(ILogger logger, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to begin protocol recording for file {FilePath} in job {JobId}")]
    private static partial void LogProtocolBeginFailed(ILogger logger, string filePath, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Excluded file {FilePath} from review in job {JobId} (matched pattern: {Pattern})")]
    private static partial void LogFileExcluded(ILogger logger, string filePath, string pattern, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "File {FilePath} classified as tier {Tier} ({ChangedLines} changed lines) in job {JobId}")]
    private static partial void LogTierAssigned(
        ILogger logger,
        string filePath,
        FileComplexityTier tier,
        int changedLines,
        Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "MaxIterationsOverride={MaxIterationsOverride} applied for file {FilePath} in job {JobId}")]
    private static partial void LogMaxIterationsOverrideApplied(
        ILogger logger,
        int maxIterationsOverride,
        string filePath,
        Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{CrossCuttingCount} cross-cutting concern(s) identified in synthesis for job {JobId}")]
    private static partial void LogCrossCuttingConcernsFound(ILogger logger, int crossCuttingCount, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{DismissalCount} dismissal pattern(s) injected into context for file {FilePath} in job {JobId}")]
    private static partial void LogDismissalsInjected(ILogger logger, int dismissalCount, string filePath, Guid jobId);

    // ── Post-processing filter log messages (feature 023) ────────────────────────

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dropped {DroppedCount} speculative comment(s) from {FilePath} for job {JobId}")]
    private static partial void LogSpeculativeCommentsDropped(
        ILogger logger,
        int droppedCount,
        string filePath,
        Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dropped {DroppedCount} INFO comment(s) from {FilePath} for job {JobId}")]
    private static partial void LogInfoCommentsDropped(ILogger logger, int droppedCount, string filePath, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dropped {DroppedCount} vague suggestion(s) from {FilePath} for job {JobId}")]
    private static partial void LogVagueSuggestionsDropped(
        ILogger logger,
        int droppedCount,
        string filePath,
        Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "Downgraded {DowngradedCount} comment severity(ies) in {FilePath} for job {JobId} (confidence floor applied)")]
    private static partial void LogSeverityDowngraded(ILogger logger, int downgradedCount, string filePath, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Quality filter started for job {JobId}: {CommentCount} comments before filter")]
    private static partial void LogQualityFilterStarted(ILogger logger, Guid jobId, int commentCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Quality filter completed for job {JobId}: {Before} → {After} comments")]
    private static partial void LogQualityFilterCompleted(ILogger logger, Guid jobId, int before, int after);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Quality filter failed for job {JobId} — using pre-filter comment list")]
    private static partial void LogQualityFilterFailed(ILogger logger, Guid jobId, Exception ex);

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

    private static int CountSeverityDowngrades(
        IReadOnlyList<ReviewComment> before,
        IReadOnlyList<ReviewComment> after)
    {
        var downgradedCount = 0;

        for (var i = 0; i < before.Count && i < after.Count; i++)
        {
            if (before[i].Severity != after[i].Severity)
            {
                downgradedCount++;
            }
        }

        return downgradedCount;
    }
}
