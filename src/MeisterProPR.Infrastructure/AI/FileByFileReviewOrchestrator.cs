// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
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
    IAiRuntimeResolver? aiRuntimeResolver = null) : IFileByFileReviewOrchestrator
{
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
                    ct);
            }

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
                    ct,
                    name: "ai_call_failure",
                    error: ex.Message);

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
        IReadOnlyList<ReviewComment> crossCuttingComments = [];
        string? synthesisInputSample = null;
        try
        {
            var expectsJson = allComments.Count > 0;
            var systemPrompt = ReviewPrompts.BuildSynthesisSystemPrompt(baseContext, expectsJson);
            var userMessage = ReviewPrompts.BuildSynthesisUserMessage(
                perFileSummaries,
                pr.Title,
                pr.Description,
                allComments);
            synthesisInputSample = userMessage;
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, systemPrompt),
                new(ChatRole.User, userMessage),
            };

            var response = await effectiveClient.GetResponseAsync(
                messages,
                new ChatOptions { ModelId = baseContext.ModelId },
                ct);

            var responseText = response.Text ?? string.Empty;
            var totalInputTokens = response.Usage?.InputTokenCount ?? 0;
            var totalOutputTokens = response.Usage?.OutputTokenCount ?? 0;

            if (TryParseSynthesisResponse(responseText, out var parsedSummary, out var parsedCrossCuttingComments))
            {
                finalSummary = parsedSummary;
                crossCuttingComments = parsedCrossCuttingComments;
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
                    new ChatOptions { ModelId = baseContext.ModelId },
                    ct);

                totalInputTokens += repairResponse.Usage?.InputTokenCount ?? 0;
                totalOutputTokens += repairResponse.Usage?.OutputTokenCount ?? 0;

                var repairedText = repairResponse.Text ?? string.Empty;
                if (TryParseSynthesisResponse(repairedText, out parsedSummary, out parsedCrossCuttingComments))
                {
                    finalSummary = parsedSummary;
                    crossCuttingComments = parsedCrossCuttingComments;
                    LogSynthesisJsonRepairSucceeded(logger, job.Id);
                }
                else
                {
                    finalSummary = string.Join("\n\n", perFileSummaries.Select(s => $"## {s.FilePath}\n{s.Summary}"));
                    crossCuttingComments = [];
                    LogSynthesisJsonRepairFailed(logger, job.Id);
                }
            }
            else
            {
                finalSummary = responseText;
                crossCuttingComments = [];
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
            crossCuttingComments = [];

            if (protocolId.HasValue)
            {
                await protocolRecorder.RecordAiCallAsync(
                    protocolId.Value,
                    1,
                    0,
                    0,
                    synthesisInputSample,
                    null,
                    ct,
                    name: "ai_call_synthesis_failed",
                    error: ex.Message);

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

        var combinedComments = crossCuttingComments.Count > 0
            ? crossCuttingComments.Concat(deduped).ToList()
            : (IReadOnlyList<ReviewComment>)deduped;

        LogCrossCuttingConcernsFound(logger, crossCuttingComments.Count, job.Id);
        return new ReviewResult(finalSummary, combinedComments)
        {
            CarriedForwardCandidatesSkipped = carriedForwardCandidatesSkipped,
        };
    }

    private static IReadOnlyList<ReviewComment> ParseCrossCuttingConcerns(string? responseText)
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

            var result = new List<ReviewComment>();
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

                result.Add(new ReviewComment(null, null, severity, message));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static bool TryParseSynthesisResponse(
        string? responseText,
        out string summary,
        out IReadOnlyList<ReviewComment> crossCuttingComments)
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
               - "cross_cutting_concerns": array of objects with keys "message" and "severity"

               Escape any quotes inside string values correctly.
               Do NOT use markdown fences.
               Do NOT add any prose before or after the JSON.
               The first character must be '{' and the last character must be '}'.
               """;
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
                    lineNumber = lnEl.GetInt32();
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

                result.Add(new ReviewComment(filePath, lineNumber, severity, message));
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
                new ChatOptions { ModelId = baseContext.ModelId ?? this._opts.ModelId },
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

                return sev == c.Severity ? c : new ReviewComment(c.FilePath, c.LineNumber, sev, c.Message);
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
