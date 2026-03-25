using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Agentic AI review implementation that supports tool calls during the review loop.
///     Wraps an <see cref="IChatClient" /> and drives a multi-turn conversation until
///     the AI signals completion or the iteration limit is reached.
/// </summary>
public sealed partial class ToolAwareAiReviewCore(
    IChatClient chatClient,
    IOptions<AiReviewOptions> options,
    ILogger<ToolAwareAiReviewCore> logger) : IAiReviewCore
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

        string systemPrompt;
        string userMessage;

        if (systemContext.PerFileHint is { } hint)
        {
            // Per-file review path (US4): use file-scoped prompts
            systemPrompt = ReviewPrompts.BuildPerFileSystemPrompt(systemContext, hint.FilePath, hint.FileIndex, hint.TotalFiles);
            userMessage = pullRequest.ChangedFiles.Count == 1
                ? ReviewPrompts.BuildPerFileUserMessage(
                    pullRequest.ChangedFiles[0],
                    hint.FileIndex,
                    hint.TotalFiles,
                    hint.AllChangedFiles,
                    pullRequest.ExistingThreads ?? [],
                    pullRequest.Title,
                    pullRequest.SourceBranch,
                    pullRequest.TargetBranch)
                : ReviewPrompts.BuildUserMessage(pullRequest);
        }
        else
        {
            // Whole-PR review path (default)
            systemPrompt = ReviewPrompts.BuildSystemPrompt(systemContext);
            userMessage = ReviewPrompts.BuildUserMessage(pullRequest);
        }

        state.Messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
        state.Messages.Add(new ChatMessage(ChatRole.User, userMessage));

        var registeredTools = BuildTools(systemContext.ReviewTools, cancellationToken);
        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = 8192,
            Tools = registeredTools.Count > 0 ? [.. registeredTools] : null,
        };

        var lastTextResponse = "";

        using var activity = ActivitySource.StartActivity("ReviewLoop");
        activity?.SetTag("pr.id", pullRequest.PullRequestId);
        activity?.SetTag("pr.iteration", pullRequest.IterationId);

        LogReviewLoopStarted(logger, pullRequest.PullRequestId, pullRequest.IterationId, opts.MaxIterations);

        while (state.Iteration <= opts.MaxIterations)
        {
            LogIterationStarted(logger, state.Iteration, opts.MaxIterations);

            // Capture input sample BEFORE AddRange so we get the last message that was sent to the AI.
            // Tool result messages have FunctionResultContent (no .Text), so serialize them explicitly.
            var inputSample = GetInputSample(state.Messages);

            var response = await chatClient.GetResponseAsync(state.Messages, chatOptions, cancellationToken);
            state.Messages.AddRange(response.Messages);
            var responseMessage = response.Messages.Last();

            var inputTokens = response.Usage?.InputTokenCount;
            var outputTokens = response.Usage?.OutputTokenCount;
            state.AccumulateTokens(inputTokens, outputTokens);

            if (systemContext.ActiveProtocolId.HasValue && systemContext.ProtocolRecorder is not null)
            {
                // When the response is function calls only, .Text is null; summarise the calls instead.
                var outputSample = responseMessage.Text
                                   ?? GetFunctionCallSummary(responseMessage);
                await systemContext.ProtocolRecorder.RecordAiCallAsync(
                    systemContext.ActiveProtocolId.Value,
                    state.Iteration,
                    inputTokens,
                    outputTokens,
                    inputSample,
                    outputSample,
                    cancellationToken);
            }

            var functionCalls = responseMessage.Contents.OfType<FunctionCallContent>().ToList();

            if (functionCalls.Count > 0)
            {
                LogToolCallsReceived(logger, functionCalls.Count, state.Iteration);

                var toolResultContents = new List<AIContent>();
                foreach (var call in functionCalls)
                {
                    var resultText = await this.InvokeToolAsync(call, registeredTools, cancellationToken);
                    toolResultContents.Add(new FunctionResultContent(call.CallId, resultText));
                    state.RecordToolCall(call.Name ?? "", JsonSerializer.Serialize(call.Arguments), resultText);

                    if (systemContext.ActiveProtocolId.HasValue && systemContext.ProtocolRecorder is not null)
                    {
                        await systemContext.ProtocolRecorder.RecordToolCallAsync(
                            systemContext.ActiveProtocolId.Value,
                            call.Name ?? "",
                            JsonSerializer.Serialize(call.Arguments),
                            resultText,
                            cancellationToken);
                    }
                }

                state.Messages.Add(new ChatMessage(ChatRole.Tool, toolResultContents));
                state.Iteration++;
                continue;
            }

            var text = response.Text ?? "";
            if (!string.IsNullOrWhiteSpace(text))
            {
                lastTextResponse = text;
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
                if (allMeetThreshold || dto.LoopComplete)
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
            state.Messages.Add(
                new ChatMessage(
                    ChatRole.User,
                    "Iteration limit reached. Please provide your final review now as a JSON object " +
                    "with summary, comments, confidence_evaluations, and loop_complete: true. " +
                    "Respond with valid JSON ONLY — no markdown fences, no preamble."));
            var finalOptions = new ChatOptions { MaxOutputTokens = chatOptions.MaxOutputTokens };
            var finalResponse = await chatClient.GetResponseAsync(state.Messages, finalOptions, cancellationToken);
            state.AccumulateTokens(finalResponse.Usage?.InputTokenCount, finalResponse.Usage?.OutputTokenCount);
            lastTextResponse = finalResponse.Text ?? "";

            if (string.IsNullOrWhiteSpace(lastTextResponse))
            {
                throw new InvalidOperationException(
                    $"AI returned an empty response for the forced final review of PR {pullRequest.PullRequestId}. " +
                    "Review job cannot be completed.");
            }
        }

        activity?.SetTag("loop.iterations", state.Iteration);
        activity?.SetTag("loop.tool_calls", state.ToolCallCount);

        LogReviewLoopFinished(logger, pullRequest.PullRequestId, state.Iteration, state.ToolCallCount);

        systemContext.LoopMetrics = BuildLoopMetrics(state);

        return ParseReviewResult(lastTextResponse);
    }

    private static List<AIFunction> BuildTools(IReviewContextTools? reviewTools, CancellationToken cancellationToken)
    {
        if (reviewTools is null)
        {
            return [];
        }

        var getChangedFiles = AIFunctionFactory.Create(
            () => reviewTools.GetChangedFilesAsync(cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "get_changed_files",
                Description = "Get the list of all files changed in this pull request, including their paths and change types.",
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
                    "Get the content of a file at a specific line range (1-based, inclusive). Use this to read full file contents when you only have a partial diff.",
            });

        return [getChangedFiles, getFileTree, getFileContent];
    }

    private async Task<string> InvokeToolAsync(
        FunctionCallContent call,
        IReadOnlyList<AIFunction> tools,
        CancellationToken cancellationToken)
    {
        var matchingTool = tools.FirstOrDefault(t => t.Name == call.Name);
        if (matchingTool is null)
        {
            return $"[Unknown tool: {call.Name}]";
        }

        try
        {
            AIFunctionArguments? functionArgs = null;
            if (call.Arguments is not null)
            {
                functionArgs = new AIFunctionArguments(call.Arguments);
            }

            var result = await matchingTool.InvokeAsync(functionArgs, cancellationToken);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            LogToolInvocationFailed(logger, call.Name ?? "", ex);
            return $"[Tool error: {ex.Message}]";
        }
    }

    private static string? GetInputSample(IList<ChatMessage> messages)
    {
        var last = messages.Count > 0 ? messages[^1] : null;
        if (last is null)
        {
            return null;
        }

        // User/assistant messages with text content
        if (last.Text is { Length: > 0 } text)
        {
            return text;
        }

        // Tool result messages: serialize each function result
        var results = last.Contents.OfType<FunctionResultContent>().ToList();
        if (results.Count > 0)
        {
            return string.Join("\n---\n", results.Select(r => $"[{r.CallId}]\n{r.Result}"));
        }

        return null;
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

    private static bool TryParseAgenticResponse(string json, out AgenticResponseDto? dto)
    {
        dto = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            dto = JsonSerializer.Deserialize<AgenticResponseDto>(json, JsonOptions);
            return dto?.ConfidenceEvaluations != null;
        }
        catch
        {
            return false;
        }
    }

    private static ReviewResult ParseReviewResult(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ReviewResult("", new List<ReviewComment>().AsReadOnly());
        }

        var dto = JsonSerializer.Deserialize<AgenticResponseDto>(json, JsonOptions);
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

        return new ReviewLoopMetrics(
            state.ToolCallCount,
            toolCallsJson,
            confidenceJson,
            finalConfidence,
            state.TotalInputTokens,
            state.TotalOutputTokens,
            state.Iteration);
    }

    [LoggerMessage(Level = LogLevel.Trace, Message = "Agentic review loop started for PR#{PrId} iteration {IterationId} (max iterations: {MaxIterations})")]
    private static partial void LogReviewLoopStarted(ILogger logger, int prId, int iterationId, int maxIterations);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Review loop iteration {Iteration}/{MaxIterations}")]
    private static partial void LogIterationStarted(ILogger logger, int iteration, int maxIterations);

    [LoggerMessage(Level = LogLevel.Debug, Message = "AI requested {ToolCallCount} tool call(s) at iteration {Iteration}")]
    private static partial void LogToolCallsReceived(ILogger logger, int toolCallCount, int iteration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Confidence snapshot recorded at iteration {Iteration} with {ScoreCount} score(s)")]
    private static partial void LogConfidenceSnapshot(ILogger logger, int iteration, int scoreCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Review loop complete at iteration {Iteration} (threshold met: {ThresholdMet}, loop_complete flag: {LoopComplete})")]
    private static partial void LogLoopComplete(ILogger logger, int iteration, bool thresholdMet, bool loopComplete);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Agentic review loop finished for PR#{PrId} after {Iterations} iteration(s) and {ToolCallCount} tool call(s)")]
    private static partial void LogReviewLoopFinished(ILogger logger, int prId, int iterations, int toolCallCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tool invocation failed for tool '{ToolName}'")]
    private static partial void LogToolInvocationFailed(ILogger logger, string toolName, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Agentic review loop for PR#{PrId} hit iteration limit at {Iteration} without a text response — forcing final review call")]
    private static partial void LogForcingFinalReview(ILogger logger, int prId, int iteration);

    private sealed record AgenticResponseDto(
        string? Summary,
        List<ReviewCommentDto>? Comments,
        List<ConfidenceEvaluationDto>? ConfidenceEvaluations,
        bool LoopComplete);

    private sealed record ReviewCommentDto(string? FilePath, int? LineNumber, string? Severity, string? Message);

    private sealed record ConfidenceEvaluationDto(string? Concern, int Confidence);
}
