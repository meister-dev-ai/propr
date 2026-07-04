// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Agentic AI review implementation that supports tool calls during the review loop.
///     Wraps an <see cref="IChatClient" /> and drives a multi-turn conversation until
///     the AI signals completion or the iteration limit is reached.
/// </summary>
internal sealed partial class ToolAwareAiReviewCore(
    IChatClient? chatClient,
    IOptions<AiReviewOptions> options,
    ILogger<ToolAwareAiReviewCore> logger,
    IManagedReviewSessionTransportFactory? managedSessionTransportFactory = null) : IAiReviewCore
{
    private static readonly ActivitySource ActivitySource = new("MeisterProPR.ReviewLoop");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public async Task<ReviewResult> ReviewAsync(
        PullRequest pullRequest,
        ReviewSystemContext systemContext,
        CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var state = new ReviewLoopState();

        string userMessage;

        if (systemContext.PerFileHint is { } hint)
        {
            // Per-file review path (US4): two System messages — global persona (S1) + per-file context (S2).
            // S1 is dropped from history on iterations 2+ to reduce retransmitted token overhead.
            state.Messages.Add(new ChatMessage(ChatRole.System, ReviewPrompts.BuildGlobalSystemPrompt(systemContext)));
            state.Messages.Add(
                new ChatMessage(
                    ChatRole.System,
                    ReviewPrompts.BuildPerFileContextPrompt(
                        systemContext,
                        hint.FilePath,
                        hint.FileIndex,
                        hint.TotalFiles)));
            userMessage = pullRequest.ChangedFiles.Count == 1
                ? ReviewPrompts.BuildPerFileUserMessage(
                    pullRequest.ChangedFiles[0],
                    hint.FileIndex,
                    hint.TotalFiles,
                    hint.AllChangedFileSummaries,
                    pullRequest.ExistingThreads ?? [],
                    pullRequest.Title,
                    pullRequest.SourceBranch,
                    pullRequest.TargetBranch,
                    systemContext)
                : ReviewPrompts.BuildUserMessage(pullRequest);

            state.SetPersistentMessages(state.Messages);
        }
        else
        {
            // Whole-PR review path (default)
            state.Messages.Add(new ChatMessage(ChatRole.System, ReviewPrompts.BuildSystemPrompt(systemContext)));
            userMessage = ReviewPrompts.BuildUserMessage(pullRequest);
            state.SetPersistentMessages(state.Messages);
        }

        state.Messages.Add(new ChatMessage(ChatRole.User, userMessage));

        if (systemContext.PerFileHint is not null)
        {
            await PromptStageEvidenceRecorder.RecordAsync(
                systemContext,
                PromptStageKeys.GlobalSystem,
                state.Messages.FirstOrDefault(message => message.Role == ChatRole.System)?.Text,
                null,
                cancellationToken);
            await PromptStageEvidenceRecorder.RecordAsync(
                systemContext,
                PromptStageKeys.PerFileContextSystem,
                state.Messages.OfType<ChatMessage>().Skip(1).FirstOrDefault(message => message.Role == ChatRole.System)?.Text,
                null,
                cancellationToken);
            await PromptStageEvidenceRecorder.RecordAsync(
                systemContext,
                PromptStageKeys.PerFileUser,
                null,
                userMessage,
                cancellationToken);
        }

        var rawTools = BuildTools(systemContext.ReviewTools, options.Value.EnableStructuralReferenceTools, cancellationToken);
        var effectiveModelId = systemContext.ModelId ?? opts.ModelId;
        var usesManagedSessionTransport = ShouldUseAgentFrameworkManagedSession(systemContext);
        var transportTools = usesManagedSessionTransport
            ? this.WrapToolsForManagedSession(rawTools, state, systemContext, opts.MaxToolResultReplayCharacters, cancellationToken)
            : rawTools;
        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = ResolveMaxOutputTokens(opts, systemContext.PerFileHint?.ComplexityTier),
            ModelId = effectiveModelId,
            Temperature = systemContext.Temperature,
            Tools = transportTools.Count > 0 ? [.. transportTools] : null,
        };

        state.InitializeSession(ResolveInitialSessionMode(systemContext));
        systemContext.ReviewSession = state.Session;

        var lastTextResponse = "";
        var seenAssistantTurns = new HashSet<string>(StringComparer.Ordinal);

        // T037: per-file override wins over the global option
        var effectiveMaxIterations = systemContext.PerFileHint?.MaxIterationsOverride ?? opts.MaxIterations;
        if (systemContext.PerFileHint?.MaxIterationsOverride is { } overrideVal)
        {
            LogMaxIterationsOverride(
                logger,
                overrideVal,
                systemContext.PerFileHint.FilePath,
                pullRequest.PullRequestId);
        }

        // T044: use tier-specific client when configured; fall back to injected default
        var effectiveClient = systemContext.TierChatClient ?? chatClient
            ?? throw new InvalidOperationException("No chat client available for review execution.");
        var managedSessionTransport = usesManagedSessionTransport
            ? (managedSessionTransportFactory ?? new ManagedReviewSessionTransportFactory()).Create(effectiveClient, transportTools)
            : null;

        Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions responseOptions)
        {
            return ShouldUseAgentFrameworkManagedSession(state, systemContext, managedSessionTransport)
                ? managedSessionTransport!.GetResponseAsync(messages, responseOptions, cancellationToken)
                : effectiveClient.GetResponseAsync(messages, responseOptions, cancellationToken);
        }

        using var activity = ActivitySource.StartActivity("ReviewLoop");
        activity?.SetTag("pr.id", pullRequest.PullRequestId);
        activity?.SetTag("pr.iteration", pullRequest.IterationId);

        LogReviewLoopStarted(logger, pullRequest.PullRequestId, pullRequest.IterationId, effectiveMaxIterations);

        try
        {
            while (state.Iteration <= effectiveMaxIterations)
            {
                LogIterationStarted(logger, state.Iteration, effectiveMaxIterations);

                ApplySessionModeToOptions(chatOptions, state);
                var messagesToSend = BuildMessagesForCurrentTurn(state, systemContext);

                // Capture input sample BEFORE AddRange so we get the last message that was sent to the AI.
                // Tool result messages have FunctionResultContent (no .Text), so serialize them explicitly.
                var inputSample = GetInputSample(messagesToSend);
                var systemPrompt = GetSystemPrompt(messagesToSend);

                ChatResponse response;
                try
                {
                    response = await GetResponseAsync(messagesToSend, chatOptions);
                }
                catch (Exception ex) when (this.TryDowngradeProviderManagedSession(state, systemContext, ex))
                {
                    ApplySessionModeToOptions(chatOptions, state);
                    messagesToSend = BuildMessagesForCurrentTurn(state, systemContext);
                    inputSample = GetInputSample(messagesToSend);
                    systemPrompt = GetSystemPrompt(messagesToSend);
                    response = await GetResponseAsync(messagesToSend, chatOptions);
                }

                AppendResponseToState(state, response);
                var responseMessage = response.Messages.Last();

                var inputTokens = response.Usage?.InputTokenCount;
                var outputTokens = response.Usage?.OutputTokenCount;
                var cachedInputTokens = response.Usage?.CachedInputTokenCount;
                state.AccumulateTokens(inputTokens, outputTokens, cachedInputTokens);
                state.UpdateContinuationHandle(CreateContinuationHandle(response, state), response.ContinuationToken);
                state.RecordTurn(
                    state.Session.Mode == AgentReviewSessionMode.StatelessReplay
                        ? ReviewTurnContextStrategy.FullContext
                        : ReviewTurnContextStrategy.DeltaContext,
                    inputSample,
                    inputTokens,
                    outputTokens);
                systemContext.ReviewSession = state.Session;

                if (systemContext.ActiveProtocolId.HasValue && systemContext.ProtocolRecorder is not null)
                {
                    // Function-call-only turns can surface as null or empty text depending on provider/client.
                    var outputSample = GetOutputSample(responseMessage);
                    await systemContext.ProtocolRecorder.RecordAiCallAsync(
                        systemContext.ActiveProtocolId.Value,
                        state.Iteration,
                        inputTokens,
                        outputTokens,
                        inputSample,
                        systemPrompt,
                        outputSample,
                        cancellationToken,
                        cachedInputTokens: cachedInputTokens,
                        cacheStatus: ResolveCacheStatus(systemContext, cachedInputTokens, messagesToSend),
                        cacheMissCategory: ResolveCacheMissCategory(systemContext, cachedInputTokens, messagesToSend),
                        prefixEligibility: ResolvePrefixEligibility(systemContext, messagesToSend));
                    await RecordSessionBindingEventIfNeededAsync(systemContext, state, cancellationToken);
                    await RecordSessionTurnEventAsync(systemContext, state, inputSample, outputSample, cancellationToken);
                }

                var functionCalls = responseMessage.Contents.OfType<FunctionCallContent>().ToList();

                var text = response.Text ?? "";
                if (!string.IsNullOrWhiteSpace(text))
                {
                    lastTextResponse = text;
                }

                var turnFingerprint = BuildAssistantTurnFingerprint(text, functionCalls);
                if (turnFingerprint is not null && !seenAssistantTurns.Add(turnFingerprint))
                {
                    LogRepeatedAssistantTurn(logger, pullRequest.PullRequestId, state.Iteration);
                    break;
                }

                if (functionCalls.Count > 0)
                {
                    LogToolCallsReceived(logger, functionCalls.Count, state.Iteration);

                    var toolResultContents = new List<AIContent>();
                    foreach (var call in functionCalls)
                    {
                        var invocation = await this.InvokeToolAsync(call, rawTools, cancellationToken);
                        var boundedResultText = invocation.BoundedResultJson;
                        toolResultContents.Add(new FunctionResultContent(call.CallId, boundedResultText));
                        state.RecordToolCall(
                            call.Name ?? "",
                            invocation.ArgumentsJson,
                            boundedResultText,
                            invocation.StartedAt,
                            invocation.CompletedAt,
                            invocation.DurationMs,
                            invocation.WaitDurationMs,
                            invocation.ActiveDurationMs,
                            invocation.TimingAvailability,
                            invocation.ToolOutcome,
                            invocation.PhaseTimings);

                        if (systemContext.ActiveProtocolId.HasValue && systemContext.ProtocolRecorder is not null)
                        {
                            await systemContext.ProtocolRecorder.RecordToolCallAsync(
                                systemContext.ActiveProtocolId.Value,
                                call.Name ?? "",
                                invocation.ArgumentsJson,
                                boundedResultText,
                                state.Iteration,
                                cancellationToken,
                                invocation.StartedAt,
                                invocation.CompletedAt,
                                invocation.DurationMs,
                                invocation.WaitDurationMs,
                                invocation.ActiveDurationMs,
                                invocation.TimingAvailability,
                                invocation.ToolOutcome,
                                invocation.PhaseTimings,
                                invocation.WasBounded ? "Bounded" : null,
                                invocation.WasBounded ? invocation.OriginalPayloadTokens : null,
                                invocation.WasBounded ? invocation.BoundedPayloadTokens : null,
                                invocation.WasBounded ? true : null);
                        }
                    }

                    state.Messages.Add(new ChatMessage(ChatRole.Tool, toolResultContents));
                    state.CompactReplayHistory();
                    systemContext.ReviewSession = state.Session;

                    state.Iteration++;
                    continue;
                }

                if (TryParseAgenticResponse(text, out var dto) && dto?.ConfidenceEvaluations is not null)
                {
                    var scores = dto.ConfidenceEvaluations
                        .Where(e => e.Concern is not null)
                        .Select(e => new ConfidenceScore(e.Concern!, e.Confidence))
                        .ToList()
                        .AsReadOnly();

                    state.RecordConfidenceSnapshot(scores);
                    LogConfidenceSnapshot(logger, state.Iteration, scores.Count);

                    var allMeetThreshold = scores.Count > 0 && scores.All(s => s.Score >= opts.ConfidenceThreshold);
                    // investigation_complete: false means the AI explicitly signals it needs more tool calls;
                    // honour that regardless of confidence scores. Only LoopComplete is an unconditional escape hatch.
                    var investigationIncomplete = dto.InvestigationComplete == false;
                    if (dto.LoopComplete || (allMeetThreshold && !investigationIncomplete))
                    {
                        LogLoopComplete(logger, state.Iteration, allMeetThreshold, dto.LoopComplete);
                        break;
                    }
                }
                else
                {
                    // Pure review JSON without confidence_evaluations — treat as final
                    break;
                }

                state.Iteration++;
            }

            // If the loop exited without ever capturing a text response (e.g., iteration limit hit
            // while the AI was still making tool calls), do one final forced call without tools so
            // the AI can emit its best-effort review from the context gathered so far.
            if (string.IsNullOrWhiteSpace(lastTextResponse))
            {
                LogForcingFinalReview(logger, pullRequest.PullRequestId, state.Iteration);
                PrepareForForcedFinalTurn(state);
                systemContext.ReviewSession = state.Session;
                state.Messages.Add(
                    new ChatMessage(
                        ChatRole.User,
                        "Iteration limit reached. Provide your final review now. " +
                        "CRITICAL: Your entire response must be a single raw JSON object with keys: " +
                        "summary, comments, confidence_evaluations, loop_complete (set to true), investigation_complete (set to true). " +
                        "Do NOT use markdown code fences. Do NOT add any text outside the JSON. " +
                        "The response must start with '{' and end with '}'."));
                var finalOptions = new ChatOptions
                    { MaxOutputTokens = chatOptions.MaxOutputTokens, ModelId = effectiveModelId, Temperature = systemContext.Temperature };
                ChatResponse finalResponse;
                try
                {
                    finalResponse = await GetResponseAsync(
                        BuildMessagesForForcedFinalTurn(state),
                        finalOptions);
                }
                catch (Exception ex) when (this.TryDowngradeProviderManagedSession(state, systemContext, ex))
                {
                    systemContext.ReviewSession = state.Session;
                    finalResponse = await GetResponseAsync(
                        BuildMessagesForForcedFinalTurn(state),
                        finalOptions);
                }

                var finalCachedInputTokens = finalResponse.Usage?.CachedInputTokenCount;
                state.AccumulateTokens(finalResponse.Usage?.InputTokenCount, finalResponse.Usage?.OutputTokenCount, finalCachedInputTokens);
                state.UpdateContinuationHandle(CreateContinuationHandle(finalResponse, state), finalResponse.ContinuationToken);
                lastTextResponse = finalResponse.Text ?? "";

                if (systemContext.ActiveProtocolId.HasValue && systemContext.ProtocolRecorder is not null)
                {
                    await systemContext.ProtocolRecorder.RecordAiCallAsync(
                        systemContext.ActiveProtocolId.Value,
                        state.Iteration,
                        finalResponse.Usage?.InputTokenCount,
                        finalResponse.Usage?.OutputTokenCount,
                        GetInputSample(BuildMessagesForForcedFinalTurn(state)),
                        GetSystemPrompt(BuildMessagesForForcedFinalTurn(state)),
                        lastTextResponse,
                        cancellationToken,
                        "ai_call_forced_final",
                        cachedInputTokens: finalCachedInputTokens,
                        cacheStatus: ResolveCacheStatus(
                            systemContext, finalCachedInputTokens, BuildMessagesForForcedFinalTurn(state)),
                        cacheMissCategory: ResolveCacheMissCategory(
                            systemContext, finalCachedInputTokens, BuildMessagesForForcedFinalTurn(state)),
                        prefixEligibility: ResolvePrefixEligibility(systemContext, BuildMessagesForForcedFinalTurn(state)),
                        finalizationAttemptKind: "ForcedFinal",
                        finalizationReason: "iteration_limit_reached",
                        finalizationOutcome: string.IsNullOrWhiteSpace(lastTextResponse) ? "StillInvalid" : "ProducedFinalText");
                }

                if (string.IsNullOrWhiteSpace(lastTextResponse))
                {
                    throw new InvalidOperationException(
                        $"AI returned an empty response for the forced final review of PR {pullRequest.PullRequestId}. " +
                        "Review job cannot be completed.");
                }
            }

            lastTextResponse = await this.RepairFinalResponseIfNeededAsync(
                effectiveClient,
                managedSessionTransport,
                state,
                chatOptions,
                effectiveModelId,
                systemContext,
                pullRequest.PullRequestId,
                lastTextResponse,
                cancellationToken);

            activity?.SetTag("loop.iterations", state.Iteration);
            activity?.SetTag("loop.tool_calls", state.ToolCallCount);

            LogReviewLoopFinished(logger, pullRequest.PullRequestId, state.Iteration, state.ToolCallCount);

            state.MarkCompleted();
            systemContext.ReviewSession = state.Session;
            systemContext.LoopMetrics = BuildLoopMetrics(state);

            return ParseReviewResult(lastTextResponse);
        }
        finally
        {
            // Ensure metrics are always captured so callers can record accurate stats even on failure.
            if (systemContext.ReviewSession is not { Status: AgentReviewSessionStatus.Completed })
            {
                state.MarkFailed();
                systemContext.ReviewSession = state.Session;
            }

            systemContext.LoopMetrics ??= BuildLoopMetrics(state);
        }
    }

    private async Task<string> RepairFinalResponseIfNeededAsync(
        IChatClient effectiveClient,
        IManagedReviewSessionTransport? managedSessionTransport,
        ReviewLoopState state,
        ChatOptions chatOptions,
        string effectiveModelId,
        ReviewSystemContext systemContext,
        int pullRequestId,
        string lastTextResponse,
        CancellationToken cancellationToken)
    {
        if (!NeedsFinalResponseRepair(lastTextResponse))
        {
            return lastTextResponse;
        }

        LogForcingSchemaCorrection(logger, pullRequestId, state.Iteration);
        state.Messages.Add(new ChatMessage(ChatRole.Assistant, lastTextResponse));
        state.Messages.Add(
            new ChatMessage(
                ChatRole.User,
                "Your previous response was invalid for the required final review output. You MUST reformat it now. " +
                "Output a single raw JSON object with EXACTLY these keys: " +
                "\"summary\" (plain string), " +
                "\"comments\" (array — move ALL review findings here as {\"file_path\": \"...\", \"line_number\": <int|null>, \"severity\": \"info\"|\"warning\"|\"error\"|\"suggestion\", \"message\": \"...\"}), " +
                "\"confidence_evaluations\" (array), " +
                "\"investigation_complete\": true, " +
                "\"loop_complete\": true. " +
                "The response must start with '{' and end with '}'. No markdown fences. No other keys."));

        var correctionOptions = new ChatOptions
            { MaxOutputTokens = chatOptions.MaxOutputTokens, ModelId = effectiveModelId, Temperature = systemContext.Temperature };
        ChatResponse correctionResponse;
        try
        {
            correctionResponse = ShouldUseAgentFrameworkManagedSession(state, systemContext, managedSessionTransport)
                ? await managedSessionTransport!.GetResponseAsync(state.Messages, correctionOptions, cancellationToken)
                : await effectiveClient.GetResponseAsync(state.Messages, correctionOptions, cancellationToken);
        }
        catch (Exception ex) when (this.TryDowngradeProviderManagedSession(state, systemContext, ex))
        {
            systemContext.ReviewSession = state.Session;
            correctionResponse = await effectiveClient.GetResponseAsync(state.Messages, correctionOptions, cancellationToken);
        }

        var correctionCachedInputTokens = correctionResponse.Usage?.CachedInputTokenCount;
        state.AccumulateTokens(
            correctionResponse.Usage?.InputTokenCount,
            correctionResponse.Usage?.OutputTokenCount,
            correctionCachedInputTokens);

        var corrected = correctionResponse.Text ?? string.Empty;
        if (systemContext.ActiveProtocolId.HasValue && systemContext.ProtocolRecorder is not null)
        {
            await systemContext.ProtocolRecorder.RecordAiCallAsync(
                systemContext.ActiveProtocolId.Value,
                state.Iteration,
                correctionResponse.Usage?.InputTokenCount,
                correctionResponse.Usage?.OutputTokenCount,
                GetInputSample(state.Messages),
                GetSystemPrompt(state.Messages),
                corrected,
                cancellationToken,
                "ai_call_schema_repair",
                cachedInputTokens: correctionCachedInputTokens,
                cacheStatus: ResolveCacheStatus(systemContext, correctionCachedInputTokens, state.Messages),
                cacheMissCategory: ResolveCacheMissCategory(
                    systemContext, correctionCachedInputTokens, state.Messages),
                prefixEligibility: ResolvePrefixEligibility(systemContext, state.Messages),
                finalizationAttemptKind: "SchemaRepair",
                finalizationReason: "malformed_or_incomplete_final_response",
                finalizationOutcome: string.IsNullOrWhiteSpace(corrected) ? "StillInvalid" : "ProducedValidCandidate");
        }

        return string.IsNullOrWhiteSpace(corrected)
            ? lastTextResponse
            : corrected;
    }

    private static AgentReviewSessionMode ResolveInitialSessionMode(ReviewSystemContext systemContext)
    {
        if (systemContext.PerFileHint is null)
        {
            return AgentReviewSessionMode.StatelessReplay;
        }

        return systemContext.RuntimeCapabilities.SupportsManagedRemoteConversation
            ? AgentReviewSessionMode.ProviderManagedSession
            : AgentReviewSessionMode.LocalManagedSession;
    }

    private static int ResolveMaxOutputTokens(AiReviewOptions opts, FileComplexityTier? tier)
    {
        return tier switch
        {
            FileComplexityTier.Low => opts.MaxOutputTokensLow,
            FileComplexityTier.Medium => opts.MaxOutputTokensMedium,
            _ => opts.MaxOutputTokensHigh,
        };
    }

    private static CacheCallStatus ResolveCacheStatus(
        ReviewSystemContext systemContext,
        long? cachedInputTokens,
        IList<ChatMessage> messages)
    {
        var eligibility = ResolvePrefixEligibility(systemContext, messages);
        if (eligibility is PrefixEligibilityStatus.NotApplicable)
        {
            return CacheCallStatus.NotApplicable;
        }

        if (!systemContext.RuntimeCapabilities.SupportsPromptCaching)
        {
            return CacheCallStatus.Unsupported;
        }

        if (eligibility is not PrefixEligibilityStatus.Eligible)
        {
            return CacheCallStatus.Ineligible;
        }

        if (!cachedInputTokens.HasValue)
        {
            return CacheCallStatus.Unobservable;
        }

        return cachedInputTokens.Value > 0
            ? CacheCallStatus.Hit
            : CacheCallStatus.Miss;
    }

    private static string? ResolveCacheMissCategory(
        ReviewSystemContext systemContext,
        long? cachedInputTokens,
        IList<ChatMessage> messages)
    {
        var status = ResolveCacheStatus(systemContext, cachedInputTokens, messages);
        return status switch
        {
            CacheCallStatus.Unsupported => "provider_unsupported",
            CacheCallStatus.Unobservable => "provider_detail_unavailable",
            CacheCallStatus.Ineligible => "prefix_changed",
            CacheCallStatus.Miss => "provider_cache_expired",
            CacheCallStatus.RoutingOverflow => "provider_routing_overflow",
            _ => null,
        };
    }

    private static PrefixEligibilityStatus ResolvePrefixEligibility(ReviewSystemContext systemContext, IList<ChatMessage> messages)
    {
        if (systemContext.PerFileHint is null || messages.Count == 0)
        {
            return PrefixEligibilityStatus.NotApplicable;
        }

        var systemPrefix = GetSystemPrompt(messages);
        if (string.IsNullOrWhiteSpace(systemPrefix))
        {
            return PrefixEligibilityStatus.NotApplicable;
        }

        return EstimateTokenCount(systemPrefix) >= 1024
            ? PrefixEligibilityStatus.Eligible
            : PrefixEligibilityStatus.IneligibleTooShort;
    }

    private static string BoundToolResult(string resultText, int maxCharacters)
    {
        if (resultText.Length <= maxCharacters)
        {
            return resultText;
        }

        var omitted = resultText.Length - maxCharacters;
        return resultText[..maxCharacters] +
               $"\n\n[Tool evidence bounded: omitted {omitted} characters from replay. Re-run the tool with a narrower range if exact context is needed.]";
    }

    private static int EstimateTokenCount(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return Math.Max(1, text.Length / 4);
    }

    private static bool ShouldUseAgentFrameworkManagedSession(ReviewSystemContext systemContext)
    {
        return systemContext.PerFileHint is not null &&
               systemContext.RuntimeCapabilities.SupportsManagedRemoteConversation;
    }

    private static bool ShouldUseAgentFrameworkManagedSession(
        ReviewLoopState state,
        ReviewSystemContext systemContext,
        IManagedReviewSessionTransport? managedSessionTransport)
    {
        return managedSessionTransport is not null &&
               state.Session.Mode == AgentReviewSessionMode.ProviderManagedSession &&
               ShouldUseAgentFrameworkManagedSession(systemContext);
    }

    private static void ApplySessionModeToOptions(ChatOptions chatOptions, ReviewLoopState state)
    {
        chatOptions.ConversationId = null;
        chatOptions.ContinuationToken = null;

        if (state.Session.Mode != AgentReviewSessionMode.ProviderManagedSession)
        {
            return;
        }

        var handle = state.Session.ContinuationHandle;
        if (state.Iteration == 1 && !string.IsNullOrWhiteSpace(handle?.ProviderSessionId))
        {
            chatOptions.ConversationId = handle.ProviderSessionId;
        }

        if (state.ProviderContinuationToken is not null)
        {
            chatOptions.ContinuationToken = state.ProviderContinuationToken;
        }
    }

    private static IList<ChatMessage> BuildMessagesForCurrentTurn(ReviewLoopState state, ReviewSystemContext systemContext)
    {
        return state.Session.Mode switch
        {
            AgentReviewSessionMode.ProviderManagedSession => BuildProviderManagedMessages(state, systemContext),
            AgentReviewSessionMode.LocalManagedSession => BuildLocalManagedMessages(state, systemContext),
            _ => BuildStatelessReplayMessages(state, systemContext),
        };
    }

    private static IList<ChatMessage> BuildProviderManagedMessages(ReviewLoopState state, ReviewSystemContext systemContext)
    {
        if (state.Iteration == 1 || state.Session.ContinuationHandle is null)
        {
            return BuildStatelessReplayMessages(state, systemContext);
        }

        return GetProviderContinuationMessages(state);
    }

    private static IList<ChatMessage> BuildLocalManagedMessages(ReviewLoopState state, ReviewSystemContext systemContext)
    {
        if (state.Iteration == 1)
        {
            return BuildStatelessReplayMessages(state, systemContext);
        }

        var messages = state.PersistentMessages.ToList();
        if (systemContext.PerFileHint is not null && messages.Count > 0)
        {
            messages = messages.Skip(1).ToList();
        }

        if (!string.IsNullOrWhiteSpace(state.CompactedPayloadSummary))
        {
            messages.Add(
                new ChatMessage(
                    ChatRole.System,
                    $"Working memory summary for prior bulky context: {state.CompactedPayloadSummary}"));
        }

        messages.AddRange(GetContinuationMessages(state));

        return messages;
    }

    private static IList<ChatMessage> BuildStatelessReplayMessages(ReviewLoopState state, ReviewSystemContext systemContext)
    {
        return state.Iteration > 1 && systemContext.PerFileHint is not null
            ? (IList<ChatMessage>)state.Messages.Skip(1).ToList()
            : state.Messages;
    }

    private static IList<ChatMessage> BuildMessagesForForcedFinalTurn(ReviewLoopState state)
    {
        return state.Session.Mode == AgentReviewSessionMode.ProviderManagedSession && state.Messages.Count > 0
            ? [state.Messages[^1]]
            : state.Messages;
    }

    private static void PrepareForForcedFinalTurn(ReviewLoopState state)
    {
        if (!HasDanglingFunctionCall(state.Messages))
        {
            return;
        }

        state.Messages.RemoveAt(state.Messages.Count - 1);
        if (state.Session.Mode == AgentReviewSessionMode.ProviderManagedSession)
        {
            state.RecordFallback(
                AgentReviewSessionMode.LocalManagedSession,
                "provider_session_forced_final_after_unhandled_tool_call",
                "dropped unresolved provider tool call before forced final response");
        }
    }

    private static IList<ChatMessage> GetContinuationMessages(ReviewLoopState state)
    {
        if (state.Messages.Count == 0)
        {
            return [];
        }

        var transientStartIndex = Math.Min(state.PersistentMessages.Count, state.Messages.Count);
        var transientMessages = state.Messages.Skip(transientStartIndex).ToList();
        if (transientMessages.Count > 0)
        {
            return transientMessages;
        }

        return [state.Messages[^1]];
    }

    private static IList<ChatMessage> GetProviderContinuationMessages(ReviewLoopState state)
    {
        if (state.Messages.Count == 0)
        {
            return [];
        }

        // Provider-managed sessions already retain earlier user/assistant turns server-side.
        // Follow-up requests should submit only the newly produced local continuation message,
        // typically the tool result that answers the provider's last function call.
        var latestMessage = state.Messages[^1];
        return latestMessage.Role == ChatRole.System
            ? []
            : [latestMessage];
    }

    private static bool HasDanglingFunctionCall(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return false;
        }

        var latestMessage = messages[^1];
        return latestMessage.Role == ChatRole.Assistant &&
               latestMessage.Contents.OfType<FunctionCallContent>().Any() &&
               !latestMessage.Contents.OfType<FunctionResultContent>().Any();
    }

    private static void AppendResponseToState(ReviewLoopState state, ChatResponse response)
    {
        if (state.Session.Mode == AgentReviewSessionMode.ProviderManagedSession &&
            state.Iteration > 1 &&
            state.Session.ContinuationHandle is not null)
        {
            state.Messages.Clear();
            state.Messages.AddRange(state.PersistentMessages);
        }

        state.Messages.AddRange(response.Messages);
    }

    private static SessionContinuationHandle? CreateContinuationHandle(ChatResponse response, ReviewLoopState state)
    {
        if (state.Session.Mode != AgentReviewSessionMode.ProviderManagedSession)
        {
            return null;
        }

        var previousHandle = state.Session.ContinuationHandle;
        var conversationId = response.ConversationId;
        var responseId = response.ResponseId;
        var providerSessionId = conversationId ?? previousHandle?.ProviderSessionId;
        var providerResponseId = responseId ?? previousHandle?.ProviderResponseId;

        if (string.IsNullOrWhiteSpace(providerSessionId) && string.IsNullOrWhiteSpace(providerResponseId))
        {
            return previousHandle;
        }

        return new SessionContinuationHandle(
            !string.IsNullOrWhiteSpace(providerSessionId)
                ? SessionContinuationHandleType.ProviderSession
                : SessionContinuationHandleType.ProviderResponseChain,
            providerSessionId ?? providerResponseId,
            providerSessionId,
            providerResponseId,
            DateTimeOffset.UtcNow);
    }

    private static async Task RecordSessionBindingEventIfNeededAsync(
        ReviewSystemContext systemContext,
        ReviewLoopState state,
        CancellationToken cancellationToken)
    {
        if (!systemContext.ActiveProtocolId.HasValue ||
            systemContext.ProtocolRecorder is null ||
            state.Session.Mode != AgentReviewSessionMode.ProviderManagedSession ||
            state.Iteration != 1 ||
            string.IsNullOrWhiteSpace(state.Session.RemoteConversationId))
        {
            return;
        }

        await systemContext.ProtocolRecorder.RecordReviewStrategyEventAsync(
            systemContext.ActiveProtocolId.Value,
            ReviewProtocolEventNames.ReviewAgentSessionBinding,
            JsonSerializer.Serialize(
                new
                {
                    sessionOwnerId = state.Session.LocalSessionId,
                    conversationOwnerId = state.Session.ConversationOwnerId,
                    bindingMethod = string.IsNullOrWhiteSpace(state.Session.ContinuationHandle?.ProviderSessionId)
                        ? "reused_remote_binding"
                        : "created_remote_thread",
                    bindingOutcome = "succeeded",
                    promptMode = state.Session.ActivePromptMode,
                    remoteConversationId = state.Session.RemoteConversationId,
                    sessionMode = state.Session.Mode,
                }),
            JsonSerializer.Serialize(
                new
                {
                    providerResponseId = state.Session.ContinuationHandle?.ProviderResponseId,
                }),
            null,
            cancellationToken);
    }

    private bool TryDowngradeProviderManagedSession(
        ReviewLoopState state,
        ReviewSystemContext systemContext,
        Exception ex)
    {
        if (state.Session.Mode != AgentReviewSessionMode.ProviderManagedSession)
        {
            return false;
        }

        var managedTransportException = ex as ManagedReviewSessionTransportException;
        AppendRecoveredMessages(state, managedTransportException?.RecoveredMessages);

        var isContinuationFailure = managedTransportException?.ContinuationStarted == true ||
                                    state.Iteration > 1 ||
                                    state.Session.ContinuationHandle is not null;

        state.RecordFallback(
            AgentReviewSessionMode.LocalManagedSession,
            isContinuationFailure
                ? "provider_session_continue_failed"
                : "provider_session_create_failed",
            "preserved durable system prompts and latest turn transcript");
        state.SetPersistentMessages(state.PersistentMessages.Count > 0 ? state.PersistentMessages : state.Messages);
        systemContext.ReviewSession = state.Session;
        this.RecordSessionFallbackEventIfNeededAsync(systemContext, state, CancellationToken.None).GetAwaiter().GetResult();
        LogProviderManagedSessionDowngraded(logger, state.Iteration, ex.Message);
        return true;
    }

    private static void AppendRecoveredMessages(ReviewLoopState state, IReadOnlyList<ChatMessage>? recoveredMessages)
    {
        if (recoveredMessages is null || recoveredMessages.Count == 0)
        {
            return;
        }

        state.Messages.AddRange(recoveredMessages);
    }

    private static async Task RecordSessionTurnEventAsync(
        ReviewSystemContext systemContext,
        ReviewLoopState state,
        string? inputSample,
        string? outputSample,
        CancellationToken cancellationToken)
    {
        if (!systemContext.ActiveProtocolId.HasValue || systemContext.ProtocolRecorder is null)
        {
            return;
        }

        var latestTurn = state.TurnHistory.Count > 0 ? state.TurnHistory[^1] : null;
        await systemContext.ProtocolRecorder.RecordReviewStrategyEventAsync(
            systemContext.ActiveProtocolId.Value,
            ReviewProtocolEventNames.ReviewAgentSessionTurn,
            JsonSerializer.Serialize(
                new
                {
                    turnNumber = latestTurn?.TurnNumber ?? state.Iteration,
                    sessionMode = state.Session.Mode,
                    contextStrategy = latestTurn?.ContextStrategy,
                    promptMode = latestTurn?.PromptMode ?? state.Session.ActivePromptMode,
                    newInputSummary = latestTurn?.NewInputSummary ?? inputSample,
                    replayedPayloadSummary = latestTurn?.ReplayedPayloadSummary,
                    compactedPayloadSummary = latestTurn?.CompactedPayloadSummary,
                    usedRemoteConversation = latestTurn?.UsedRemoteConversation,
                    usedLocalReplay = latestTurn?.UsedLocalReplay,
                    remoteConversationId = latestTurn?.RemoteConversationId,
                    providerSessionId = latestTurn?.ProviderSessionId,
                    providerResponseId = latestTurn?.ProviderResponseId,
                }),
            JsonSerializer.Serialize(
                new
                {
                    outputSample,
                    continuationHandle = latestTurn?.ContinuationHandle,
                }),
            null,
            cancellationToken);

        if (state.Session.Fallbacks.Count == 0)
        {
            return;
        }

        var fallback = state.Session.Fallbacks[^1];
        if (fallback.TurnNumber != state.Iteration)
        {
            return;
        }

        await systemContext.ProtocolRecorder.RecordReviewStrategyEventAsync(
            systemContext.ActiveProtocolId.Value,
            ReviewProtocolEventNames.ReviewAgentSessionFallback,
            JsonSerializer.Serialize(
                new
                {
                    fromMode = fallback.FromMode,
                    toMode = fallback.ToMode,
                    fallback.Reason,
                    fallback.TurnNumber,
                    fallback.PreservedState,
                }),
            null,
            null,
            cancellationToken);
    }

    private async Task RecordSessionFallbackEventIfNeededAsync(
        ReviewSystemContext systemContext,
        ReviewLoopState state,
        CancellationToken cancellationToken)
    {
        if (!systemContext.ActiveProtocolId.HasValue || systemContext.ProtocolRecorder is null || state.Session.Fallbacks.Count == 0)
        {
            return;
        }

        var fallback = state.Session.Fallbacks[^1];
        await systemContext.ProtocolRecorder.RecordReviewStrategyEventAsync(
            systemContext.ActiveProtocolId.Value,
            ReviewProtocolEventNames.ReviewAgentSessionFallback,
            JsonSerializer.Serialize(
                new
                {
                    fromMode = fallback.FromMode,
                    toMode = fallback.ToMode,
                    fallback.Reason,
                    fallback.TurnNumber,
                    fallback.PreservedState,
                }),
            null,
            null,
            cancellationToken);
    }

    private static List<AIFunction> BuildTools(
        IReviewContextTools? reviewTools,
        bool enableStructuralReferenceTools,
        CancellationToken cancellationToken)
    {
        if (reviewTools is null)
        {
            return [];
        }

        var supportsProCursorTools = reviewTools is not IProCursorAvailabilityAware { SupportsProCursorTools: false };

        var getChangedFiles = AIFunctionFactory.Create(
            () => reviewTools.GetChangedFilesAsync(cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "get_changed_files",
                Description =
                    "Get the list of all files changed in this pull request, including their paths and change types.",
            });

        var getFileTree = AIFunctionFactory.Create(
            (string branch) => reviewTools.GetFileTreeAsync(branch, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "get_file_tree",
                Description = "Get the full file tree of the repository at the specified branch.",
            });

        var getFileContent = AIFunctionFactory.Create(
            (string path, string branch, int startLine, int endLine) =>
                reviewTools.GetFileContentAsync(path, branch, startLine, endLine, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "get_file_content",
                Description =
                    "Get the content of a file at a specific line range (1-based, inclusive). Use this to read full file contents when you only have a partial diff. Always use the PR source branch (shown in the per-file header) — never main or master.",
            });

        var searchSourceChangedFiles = AIFunctionFactory.Create(
            (string searchTerm, string? fileMask) => reviewTools.SearchSourceChangedFilesAsync(searchTerm, fileMask, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = BoundedReviewContextTools.SearchSourceChangedFilesToolName,
                Description =
                    "Search only the PR changed files on the source branch using a regex searchTerm and an optional glob fileMask. Returns structured matches, limitations, and truncation metadata.",
            });

        var searchTargetRepo = AIFunctionFactory.Create(
            (string searchTerm, string? fileMask) => reviewTools.SearchTargetRepoAsync(searchTerm, fileMask, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = BoundedReviewContextTools.SearchTargetRepoToolName,
                Description =
                    "Search the PR target branch across the whole repository using a regex searchTerm and an optional glob fileMask. Use this when you need baseline behavior from the merge target.",
            });

        var searchTargetChangedFiles = AIFunctionFactory.Create(
            (string searchTerm, string? fileMask) => reviewTools.SearchTargetChangedFilesAsync(searchTerm, fileMask, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = BoundedReviewContextTools.SearchTargetChangedFilesToolName,
                Description =
                    "Search only the PR changed paths on the target branch using a regex searchTerm and an optional glob fileMask. Use this to compare changed files against their target-branch baseline.",
            });

        var searchCode = AIFunctionFactory.Create(
            (
                    string queryText,
                    string searchMode,
                    string branchSide,
                    string pathScope,
                    string? language,
                    string? fileGlob,
                    string? pathPrefix,
                    bool excludeGenerated,
                    bool excludeTests) =>
                reviewTools.SearchCodeAsync(
                    new CodeSearchRequest(
                        queryText,
                        searchMode,
                        branchSide,
                        pathScope,
                        new CodeSearchFilterSet(language, fileGlob, pathPrefix, excludeGenerated, excludeTests)),
                    cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = BoundedReviewContextTools.SearchCodeToolName,
                Description =
                    "Search code content with explicit branchSide (source|target), pathScope (repository|changed_files), searchMode (exact_identifier|exact_phrase|regex|related_symbol|related_config_key|related_route|related_dependency_registration|related_exception_or_log), and optional filters: language, fileGlob, pathPrefix, excludeGenerated, excludeTests. Returns ranked structured matches and limitations.",
            });

        var searchPaths = AIFunctionFactory.Create(
            (
                    string queryText,
                    string matchMode,
                    string branchSide,
                    string pathScope,
                    string? language,
                    string? fileGlob,
                    string? pathPrefix,
                    bool excludeGenerated,
                    bool excludeTests) =>
                reviewTools.SearchPathsAsync(
                    new PathSearchRequest(
                        queryText,
                        matchMode,
                        branchSide,
                        pathScope,
                        new CodeSearchFilterSet(language, fileGlob, pathPrefix, excludeGenerated, excludeTests)),
                    cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = BoundedReviewContextTools.SearchPathsToolName,
                Description =
                    "Search repository-relative paths without reading file content. Use matchMode contains, exact_name, or exact_path_prefix with branchSide, pathScope, and optional language/fileGlob/pathPrefix/excludeGenerated/excludeTests filters. Returns ranked paths and limitations.",
            });

        var getRepositoryOverview = AIFunctionFactory.Create(
            (string branchSide) => reviewTools.GetRepositoryOverviewAsync(branchSide, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = BoundedReviewContextTools.GetRepositoryOverviewToolName,
                Description =
                    "Get a concise structured repository overview for branch_side source or target, including projects, entry points, module boundaries, tests, config, persistence, registration, docs/specs, and limitations.",
            });

        var getFileNeighborhood = AIFunctionFactory.Create(
            (string filePath, string branchSide) => reviewTools.GetFileNeighborhoodAsync(filePath, branchSide, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = BoundedReviewContextTools.GetFileNeighborhoodToolName,
                Description =
                    "Get focused branch-specific context around one repository-relative file: owning project/module, nearby tests, config, registration/entry seams, docs/specs, and not-found limitations.",
            });

        var tools = new List<AIFunction>
        {
            getChangedFiles,
            getFileTree,
            getFileContent,
            searchSourceChangedFiles,
            searchTargetRepo,
            searchTargetChangedFiles,
            searchCode,
            searchPaths,
            getRepositoryOverview,
            getFileNeighborhood,
        };

        if (supportsProCursorTools)
        {
            tools.Add(
                AIFunctionFactory.Create(
                    (string question) => reviewTools.AskProCursorKnowledgeAsync(question, cancellationToken),
                    new AIFunctionFactoryOptions
                    {
                        Name = "ask_procursor_knowledge",
                        Description =
                            "Ask ProCursor a repository-aware knowledge question using the current review repository context. Returns sourced results with freshness metadata or an explicit no-result/unavailable status.",
                    }));

            tools.Add(
                AIFunctionFactory.Create(
                    (string symbol, string? queryMode, int? maxRelations) =>
                        reviewTools.GetProCursorSymbolInfoAsync(symbol, queryMode, maxRelations, cancellationToken),
                    new AIFunctionFactoryOptions
                    {
                        Name = "get_procursor_symbol_info",
                        Description =
                            "Ask ProCursor for symbol-aware insight using the current review repository context. Returns definitions plus related references, calls, inheritance, or containment when available.",
                    }));
        }

        // Cross-file structural reference tools, available for ALL languages (registered OUTSIDE the
        // ProCursor gate so they coexist with the C#-only ProCursor symbol tool). Gated on the
        // EnableStructuralReferenceTools kill-switch.
        if (enableStructuralReferenceTools)
        {
            tools.Add(
                AIFunctionFactory.Create(
                    (string symbol, string? branchSide) =>
                        reviewTools.FindReferencesAsync(
                            new SymbolReferenceQuery(symbol, null, string.IsNullOrWhiteSpace(branchSide) ? "source" : branchSide!),
                            cancellationToken),
                    new AIFunctionFactoryOptions
                    {
                        Name = "find_references",
                        Description =
                            "Find confirmed cross-file usage sites of a symbol across the review workspace, for any language. Returns real call/reference sites (file + line) with comment and string matches excluded; bounded with a truncation flag. Prefer this over text search for 'where is this symbol used?'.",
                    }));

            tools.Add(
                AIFunctionFactory.Create(
                    (string symbol, string? branchSide) =>
                        reviewTools.GetDefinitionAsync(
                            new SymbolReferenceQuery(symbol, null, string.IsNullOrWhiteSpace(branchSide) ? "source" : branchSide!),
                            cancellationToken),
                    new AIFunctionFactoryOptions
                    {
                        Name = "get_definition",
                        Description =
                            "Find the definition site(s) of a symbol across the review workspace, for any language. Returns kind, name, file, and 1-based line range for each candidate definition (name-based); bounded with a truncation flag.",
                    }));
        }

        return tools;
    }

    private List<AIFunction> WrapToolsForManagedSession(
        IReadOnlyList<AIFunction> tools,
        ReviewLoopState state,
        ReviewSystemContext systemContext,
        int maxToolResultReplayCharacters,
        CancellationToken cancellationToken)
    {
        return tools
            .Select(tool => new RecordingAIFunction(
                tool, logger, maxToolResultReplayCharacters, invocation =>
                    this.RecordManagedToolInvocationAsync(
                        invocation,
                        state,
                        systemContext,
                        cancellationToken)))
            .Cast<AIFunction>()
            .ToList();
    }

    private async Task RecordManagedToolInvocationAsync(
        ToolInvocationTelemetry invocation,
        ReviewLoopState state,
        ReviewSystemContext systemContext,
        CancellationToken cancellationToken)
    {
        state.RecordToolCall(
            invocation.ToolName,
            invocation.ArgumentsJson,
            invocation.BoundedResultJson,
            invocation.StartedAt,
            invocation.CompletedAt,
            invocation.DurationMs,
            invocation.WaitDurationMs,
            invocation.ActiveDurationMs,
            invocation.TimingAvailability,
            invocation.ToolOutcome,
            invocation.PhaseTimings);

        if (systemContext.ActiveProtocolId.HasValue && systemContext.ProtocolRecorder is not null)
        {
            await systemContext.ProtocolRecorder.RecordToolCallAsync(
                systemContext.ActiveProtocolId.Value,
                invocation.ToolName,
                invocation.ArgumentsJson,
                invocation.BoundedResultJson,
                state.Iteration,
                cancellationToken,
                invocation.StartedAt,
                invocation.CompletedAt,
                invocation.DurationMs,
                invocation.WaitDurationMs,
                invocation.ActiveDurationMs,
                invocation.TimingAvailability,
                invocation.ToolOutcome,
                invocation.PhaseTimings,
                invocation.WasBounded ? "Bounded" : null,
                invocation.WasBounded ? invocation.OriginalPayloadTokens : null,
                invocation.WasBounded ? invocation.BoundedPayloadTokens : null,
                invocation.WasBounded ? true : null);
        }
    }

    private async Task<ToolInvocationTelemetry> InvokeToolAsync(
        FunctionCallContent call,
        IReadOnlyList<AIFunction> tools,
        CancellationToken cancellationToken)
    {
        var toolName = call.Name ?? string.Empty;
        var argumentsJson = JsonSerializer.Serialize(call.Arguments);
        var matchingTool = tools.FirstOrDefault(t => t.Name == call.Name);
        if (matchingTool is null)
        {
            var now = DateTimeOffset.UtcNow;
            var resultText = $"[Unknown tool: {call.Name}]";
            return CreateToolInvocationTelemetry(toolName, argumentsJson, resultText, resultText, now, now, 0, ProtocolEventToolOutcomes.Failed, null);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        using var timingScope = ToolTimingCollectorContext.BeginCollection();

        try
        {
            AIFunctionArguments? functionArgs = null;
            if (call.Arguments is not null)
            {
                functionArgs = new AIFunctionArguments(call.Arguments);
            }

            var result = await matchingTool.InvokeAsync(functionArgs, cancellationToken);
            var resultText = JsonSerializer.Serialize(result, JsonOptions);
            var boundedResultText = BoundToolResult(resultText, options.Value.MaxToolResultReplayCharacters);
            return CreateToolInvocationTelemetry(
                toolName,
                argumentsJson,
                resultText,
                boundedResultText,
                startedAt,
                DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds,
                ProtocolEventToolOutcomes.Succeeded,
                ToolTimingCollectorContext.CaptureSnapshot());
        }
        catch (OperationCanceledException ex)
        {
            LogToolInvocationFailed(logger, toolName, ex);
            var resultText = $"[Tool error: {ex.Message}]";
            return CreateToolInvocationTelemetry(
                toolName,
                argumentsJson,
                resultText,
                resultText,
                startedAt,
                DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds,
                ProtocolEventToolOutcomes.Cancelled,
                ToolTimingCollectorContext.CaptureSnapshot());
        }
        catch (Exception ex)
        {
            LogToolInvocationFailed(logger, toolName, ex);
            var resultText = $"[Tool error: {ex.Message}]";
            return CreateToolInvocationTelemetry(
                toolName,
                argumentsJson,
                resultText,
                resultText,
                startedAt,
                DateTimeOffset.UtcNow,
                stopwatch.ElapsedMilliseconds,
                ProtocolEventToolOutcomes.Failed,
                ToolTimingCollectorContext.CaptureSnapshot());
        }
    }

    private static ToolInvocationTelemetry CreateToolInvocationTelemetry(
        string toolName,
        string argumentsJson,
        string resultJson,
        string boundedResultJson,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        long durationMs,
        string toolOutcome,
        IReadOnlyList<ProtocolEventPhaseTiming>? phaseTimings)
    {
        var wasBounded = boundedResultJson.Length < resultJson.Length;
        var waitDurationMs = phaseTimings?
            .Where(phase => string.Equals(phase.Name, ProtocolEventToolPhaseNames.RetryBackoff, StringComparison.Ordinal))
            .Sum(phase => phase.DurationMs ?? 0L);
        if (waitDurationMs == 0)
        {
            waitDurationMs = null;
        }

        long? activeDurationMs = waitDurationMs.HasValue
            ? Math.Max(0L, durationMs - waitDurationMs.Value)
            : null;

        return new ToolInvocationTelemetry(
            toolName,
            argumentsJson,
            resultJson,
            boundedResultJson,
            startedAt,
            completedAt,
            durationMs,
            waitDurationMs,
            activeDurationMs,
            ProtocolEventTimingAvailabilities.Captured,
            toolOutcome,
            phaseTimings,
            wasBounded,
            EstimateTokenCount(resultJson),
            EstimateTokenCount(boundedResultJson));
    }

    private static string? GetInputSample(IList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return null;
        }

        return string.Join(
            "\n\n---\n\n",
            messages
                .Select(FormatInputMessage)
                .Where(sample => !string.IsNullOrWhiteSpace(sample)));
    }

    private static string? FormatInputMessage(ChatMessage message)
    {
        if (message.Text is { Length: > 0 } text)
        {
            return $"[{message.Role.Value}]\n{text}";
        }

        var toolResults = message.Contents.OfType<FunctionResultContent>().ToList();
        if (toolResults.Count > 0)
        {
            return $"[{message.Role.Value}]\n" +
                   string.Join("\n---\n", toolResults.Select(r => $"[{r.CallId}]\n{r.Result}"));
        }

        var functionCalls = message.Contents.OfType<FunctionCallContent>().ToList();
        if (functionCalls.Count > 0)
        {
            return $"[{message.Role.Value}]\n" +
                   string.Join(
                       "\n---\n",
                       functionCalls.Select(call =>
                           $"{call.Name ?? "(unknown)"}\n{JsonSerializer.Serialize(call.Arguments)}"));
        }

        return null;
    }

    private static string? GetSystemPrompt(IList<ChatMessage> messages)
    {
        var prompts = messages
            .Where(message => message.Role == ChatRole.System && !string.IsNullOrWhiteSpace(message.Text))
            .Select(message => message.Text!)
            .ToList();

        return prompts.Count > 0
            ? string.Join("\n\n---\n\n", prompts)
            : null;
    }

    private static string? GetFunctionCallSummary(ChatMessage message)
    {
        var calls = message.Contents.OfType<FunctionCallContent>().ToList();
        if (calls.Count == 0)
        {
            return null;
        }

        return "[tool calls: " + string.Join(", ", calls.Select(c => c.Name)) + "]";
    }

    private static string? GetOutputSample(ChatMessage message)
    {
        return string.IsNullOrWhiteSpace(message.Text)
            ? GetFunctionCallSummary(message)
            : message.Text;
    }

    private static string? BuildAssistantTurnFingerprint(string text, IReadOnlyList<FunctionCallContent> functionCalls)
    {
        var normalizedText = string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : NormalizeJsonPayload(text);

        if (string.IsNullOrWhiteSpace(normalizedText) && functionCalls.Count == 0)
        {
            return null;
        }

        var toolFingerprint = functionCalls.Count == 0
            ? string.Empty
            : string.Join(
                "|",
                functionCalls.Select(call => $"{call.Name}:{JsonSerializer.Serialize(call.Arguments)}"));

        return $"{normalizedText}\n---tools---\n{toolFingerprint}";
    }

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="json" /> is a non-trivial response
    ///     that parsed successfully but with <c>Comments == null</c> (the key is entirely absent,
    ///     indicating the model invented a different schema rather than emitting an empty array).
    ///     Responses of 20 chars or fewer are considered trivially short and skipped.
    ///     Note: <see cref="JsonOptions" /> uses <c>PropertyNameCaseInsensitive = true</c>, so
    ///     PascalCase variants like <c>"Comments"</c> are already handled by the DTO deserializer.
    ///     As a secondary guard, we also scan the raw JSON for any key matching <c>"comments"</c>
    ///     case-insensitively; if found, we trust the DTO result rather than triggering a correction.
    /// </summary>
    private static bool ResponseNeedsSchemaCorrection(string json)
    {
        var normalized = NormalizeJsonPayload(json);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length <= 20)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<AgenticResponseDto>(normalized, JsonOptions);
            if (dto is null || dto.Comments is not null)
            {
                // Either unparseable or Comments was present (possibly empty — that's valid).
                return false;
            }

            // Secondary guard: scan the raw JSON for any "comments"-named key (case-insensitive).
            // This catches alternate casings or key names that the DTO normaliser already handles,
            // preventing a spurious schema-correction call when the field is genuinely present.
            using var doc = JsonDocument.Parse(normalized);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("comments", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            // Unparseable — let ParseReviewResult handle/report the error.
            return false;
        }
    }

    private static bool NeedsFinalResponseRepair(string json)
    {
        var normalized = NormalizeJsonPayload(json);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (ResponseNeedsSchemaCorrection(normalized))
        {
            return true;
        }

        try
        {
            JsonSerializer.Deserialize<AgenticResponseDto>(normalized, JsonOptions);
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool TryParseAgenticResponse(string json, out AgenticResponseDto? dto)
    {
        dto = null;
        var normalized = NormalizeJsonPayload(json);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        try
        {
            dto = JsonSerializer.Deserialize<AgenticResponseDto>(normalized, JsonOptions);
            return dto?.ConfidenceEvaluations != null;
        }
        catch
        {
            return false;
        }
    }

    private static ReviewResult ParseReviewResult(string json)
    {
        var normalized = NormalizeJsonPayload(json);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new ReviewResult("", new List<ReviewComment>().AsReadOnly());
        }

        var dto = JsonSerializer.Deserialize<AgenticResponseDto>(normalized, JsonOptions);
        if (dto is null)
        {
            return new ReviewResult("", new List<ReviewComment>().AsReadOnly());
        }

        var comments = (dto.Comments ?? []).Select(c => new ReviewComment(
                c.FilePath,
                c.LineNumber,
                Enum.TryParse<CommentSeverity>(c.Severity, true, out var sev) ? sev : CommentSeverity.Info,
                c.Message ?? ""))
            .ToList();

        return new ReviewResult(dto.Summary ?? "", comments.AsReadOnly());
    }

    private static string NormalizeJsonPayload(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        var trimmed = StripMarkdownFence(json.Trim());
        var firstBrace = trimmed.IndexOf('{');
        if (firstBrace > 0)
        {
            trimmed = trimmed[firstBrace..].TrimStart();
        }

        return TryExtractFirstJsonValue(trimmed, out var payload)
            ? payload
            : trimmed;
    }

    private static string StripMarkdownFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstNewline = text.IndexOf('\n');
        if (firstNewline >= 0)
        {
            text = text[(firstNewline + 1)..];
        }
        else
        {
            var braceStart = text.IndexOf('{');
            if (braceStart >= 0)
            {
                text = text[braceStart..];
            }
        }

        var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence >= 0)
        {
            text = text[..closingFence];
        }

        return text.Trim();
    }

    private static bool TryExtractFirstJsonValue(string text, out string payload)
    {
        payload = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read())
            {
                return false;
            }

            using var document = JsonDocument.ParseValue(ref reader);
            payload = document.RootElement.GetRawText();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ReviewLoopMetrics BuildLoopMetrics(ReviewLoopState state)
    {
        string? toolCallsJson = null;
        if (state.ToolCallHistory.Count > 0)
        {
            toolCallsJson = JsonSerializer.Serialize(state.ToolCallHistory, JsonOptions);
        }

        string? confidenceJson = null;
        if (state.ConfidenceSnapshots.Count > 0)
        {
            var allScores = state.ConfidenceSnapshots.SelectMany(s => s.Scores).ToList();
            confidenceJson = JsonSerializer.Serialize(allScores, JsonOptions);
        }

        int? finalConfidence = null;
        if (state.ConfidenceSnapshots.Count > 0)
        {
            var lastSnapshot = state.ConfidenceSnapshots[^1];
            if (lastSnapshot.Scores.Count > 0)
            {
                finalConfidence = (int)lastSnapshot.Scores.Average(s => s.Score);
            }
        }

        var turnsJson = state.TurnHistory.Count > 0
            ? JsonSerializer.Serialize(state.TurnHistory, JsonOptions)
            : null;
        var fallbacksJson = state.Session.Fallbacks.Count > 0
            ? JsonSerializer.Serialize(state.Session.Fallbacks, JsonOptions)
            : null;

        return new ReviewLoopMetrics(
            state.ToolCallCount,
            toolCallsJson,
            confidenceJson,
            finalConfidence,
            state.TotalInputTokens,
            state.TotalOutputTokens,
            state.Iteration,
            state.Session.Mode,
            turnsJson,
            fallbacksJson,
            state.Session.ContinuationHandle?.ProviderSessionId,
            state.Session.ContinuationHandle?.ProviderResponseId,
            state.Session.ActivePromptMode,
            state.TotalCachedInputTokens);
    }

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message =
            "Agentic review loop started for PR#{PrId} iteration {IterationId} (max iterations: {MaxIterations})")]
    private static partial void LogReviewLoopStarted(ILogger logger, int prId, int iterationId, int maxIterations);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Review loop iteration {Iteration}/{MaxIterations}")]
    private static partial void LogIterationStarted(ILogger logger, int iteration, int maxIterations);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "AI requested {ToolCallCount} tool call(s) at iteration {Iteration}")]
    private static partial void LogToolCallsReceived(ILogger logger, int toolCallCount, int iteration);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Confidence snapshot recorded at iteration {Iteration} with {ScoreCount} score(s)")]
    private static partial void LogConfidenceSnapshot(ILogger logger, int iteration, int scoreCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "Review loop complete at iteration {Iteration} (threshold met: {ThresholdMet}, loop_complete flag: {LoopComplete})")]
    private static partial void LogLoopComplete(ILogger logger, int iteration, bool thresholdMet, bool loopComplete);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "Agentic review loop finished for PR#{PrId} after {Iterations} iteration(s) and {ToolCallCount} tool call(s)")]
    private static partial void LogReviewLoopFinished(ILogger logger, int prId, int iterations, int toolCallCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool invocation failed for tool '{ToolName}'")]
    private static partial void LogToolInvocationFailed(ILogger logger, string toolName, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Agentic review loop for PR#{PrId} hit iteration limit at {Iteration} without a text response — forcing final review call")]
    private static partial void LogForcingFinalReview(ILogger logger, int prId, int iteration);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Agentic review loop for PR#{PrId} repeated an earlier assistant response at iteration {Iteration}; using the latest response")]
    private static partial void LogRepeatedAssistantTurn(ILogger logger, int prId, int iteration);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Agentic review for PR#{PrId} at iteration {Iteration} returned wrong schema (comments key absent) — injecting schema-correction call")]
    private static partial void LogForcingSchemaCorrection(ILogger logger, int prId, int iteration);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Provider-managed agent session downgraded at iteration {Iteration}: {Reason}")]
    private static partial void LogProviderManagedSessionDowngraded(ILogger logger, int iteration, string reason);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Using MaxIterationsOverride={MaxIterationsOverride} for file {FilePath} (PR#{PrId})")]
    private static partial void LogMaxIterationsOverride(
        ILogger logger,
        int maxIterationsOverride,
        string filePath,
        int prId);

    private sealed record AgenticResponseDto(
        [property: JsonConverter(typeof(FlexibleStringConverter))]
        string? Summary,
        List<ReviewCommentDto>? Comments,
        [property: JsonConverter(typeof(LenientConfidenceListConverter))]
        List<ConfidenceEvaluationDto>? ConfidenceEvaluations,
        bool LoopComplete,
        bool? InvestigationComplete); // null = absent = treated as complete (backward compat)

    private sealed record ReviewCommentDto(string? FilePath, int? LineNumber, string? Severity, string? Message);

    private sealed record ConfidenceEvaluationDto(
        string? Concern,
        [property: JsonConverter(typeof(FlexibleIntConverter))]
        int Confidence);

    /// <summary>
    ///     Tolerates <c>"summary"</c> being a JSON string, array of strings, or any other
    ///     token type (e.g. an object).  Arrays are joined with double newlines; objects
    ///     and other non-string tokens are serialised back to their raw JSON text.
    /// </summary>
    private sealed class FlexibleStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.TokenType switch
            {
                JsonTokenType.Null => null,
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.StartArray => ReadArrayAsString(ref reader),
                _ => ReadRawAsString(ref reader),
            };
        }

        private static string ReadArrayAsString(ref Utf8JsonReader reader)
        {
            var items = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    items.Add(reader.GetString() ?? string.Empty);
                }
                else
                {
                    // Nested array/object — skip the whole value
                    reader.Skip();
                }
            }

            return string.Join("\n\n", items);
        }

        private static string ReadRawAsString(ref Utf8JsonReader reader)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            return doc.RootElement.GetRawText();
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStringValue(value);
            }
        }
    }

    /// <summary>
    ///     Tolerates <c>"confidence"</c> being a JSON number or a string-encoded number (e.g. <c>"85"</c>).
    ///     String values that cannot be parsed as an integer are treated as 0.
    /// </summary>
    private sealed class FlexibleIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                return reader.TryGetInt32(out var n) ? n : (int)reader.GetDouble();
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                var s = reader.GetString() ?? "";
                // Trim trailing % or other decoration the model might add (e.g. "85%")
                s = s.TrimEnd('%').Trim();
                return int.TryParse(s, out var parsed) ? parsed : 0;
            }

            // Null or any other token — skip and return 0
            reader.Skip();
            return 0;
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>
    ///     Deserialises a JSON array of <see cref="ConfidenceEvaluationDto" /> objects while
    ///     tolerating elements that are not objects (e.g. plain strings like <c>"correctness: 85"</c>).
    ///     Non-object elements are silently skipped so the rest of the array is still usable.
    /// </summary>
    private sealed class LenientConfidenceListConverter : JsonConverter<List<ConfidenceEvaluationDto>?>
    {
        public override List<ConfidenceEvaluationDto>? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                return null;
            }

            var list = new List<ConfidenceEvaluationDto>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    var item = JsonSerializer.Deserialize<ConfidenceEvaluationDto>(ref reader, options);
                    if (item is not null)
                    {
                        list.Add(item);
                    }
                }
                else
                {
                    // String, number, nested array, etc. — skip the whole value
                    reader.Skip();
                }
            }

            return list;
        }

        public override void Write(
            Utf8JsonWriter writer,
            List<ConfidenceEvaluationDto>? value,
            JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value, options);
        }
    }

    private sealed class RecordingAIFunction(
        AIFunction inner,
        ILogger logger,
        int maxToolResultReplayCharacters,
        Func<ToolInvocationTelemetry, Task> onInvoked) : AIFunction
    {
        public override MethodInfo? UnderlyingMethod => inner.UnderlyingMethod;

        public override JsonSerializerOptions JsonSerializerOptions => inner.JsonSerializerOptions;

        public override JsonElement JsonSchema => inner.JsonSchema;

        public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;

        public override string Name => inner.Name;

        public override string Description => inner.Description;

        public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var argumentsJson = JsonSerializer.Serialize(
                arguments.ToDictionary(argument => argument.Key, argument => argument.Value),
                JsonOptions);
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            using var timingScope = ToolTimingCollectorContext.BeginCollection();

            try
            {
                var result = await inner.InvokeAsync(arguments, cancellationToken);
                var resultJson = JsonSerializer.Serialize(result, JsonOptions);
                var boundedResultJson = BoundToolResult(resultJson, maxToolResultReplayCharacters);
                await onInvoked(
                    CreateToolInvocationTelemetry(
                        this.Name,
                        argumentsJson,
                        resultJson,
                        boundedResultJson,
                        startedAt,
                        DateTimeOffset.UtcNow,
                        stopwatch.ElapsedMilliseconds,
                        ProtocolEventToolOutcomes.Succeeded,
                        ToolTimingCollectorContext.CaptureSnapshot()));
                return boundedResultJson;
            }
            catch (OperationCanceledException ex)
            {
                LogToolInvocationFailed(logger, this.Name, ex);
                var errorResult = $"[Tool error: {ex.Message}]";
                await onInvoked(
                    CreateToolInvocationTelemetry(
                        this.Name,
                        argumentsJson,
                        errorResult,
                        errorResult,
                        startedAt,
                        DateTimeOffset.UtcNow,
                        stopwatch.ElapsedMilliseconds,
                        ProtocolEventToolOutcomes.Cancelled,
                        ToolTimingCollectorContext.CaptureSnapshot()));
                return errorResult;
            }
            catch (Exception ex)
            {
                LogToolInvocationFailed(logger, this.Name, ex);
                var errorResult = $"[Tool error: {ex.Message}]";
                await onInvoked(
                    CreateToolInvocationTelemetry(
                        this.Name,
                        argumentsJson,
                        errorResult,
                        errorResult,
                        startedAt,
                        DateTimeOffset.UtcNow,
                        stopwatch.ElapsedMilliseconds,
                        ProtocolEventToolOutcomes.Failed,
                        ToolTimingCollectorContext.CaptureSnapshot()));
                return errorResult;
            }
        }
    }

    private sealed record ToolInvocationTelemetry(
        string ToolName,
        string ArgumentsJson,
        string ResultJson,
        string BoundedResultJson,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt,
        long? DurationMs,
        long? WaitDurationMs,
        long? ActiveDurationMs,
        string TimingAvailability,
        string ToolOutcome,
        IReadOnlyList<ProtocolEventPhaseTiming>? PhaseTimings,
        bool WasBounded,
        int OriginalPayloadTokens,
        int BoundedPayloadTokens);
}
