// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
    IChatClient? chatClient,
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

        string userMessage;

        if (systemContext.PerFileHint is { } hint)
        {
            // Per-file review path (US4): two System messages — global persona (S1) + per-file context (S2).
            // S1 is dropped from history on iterations 2+ to reduce retransmitted token overhead.
            state.Messages.Add(new ChatMessage(ChatRole.System, ReviewPrompts.BuildGlobalSystemPrompt(systemContext)));
            state.Messages.Add(new ChatMessage(ChatRole.System, ReviewPrompts.BuildPerFileContextPrompt(systemContext, hint.FilePath, hint.FileIndex, hint.TotalFiles)));
            userMessage = pullRequest.ChangedFiles.Count == 1
                ? ReviewPrompts.BuildPerFileUserMessage(
                    pullRequest.ChangedFiles[0],
                    hint.FileIndex,
                    hint.TotalFiles,
                    hint.AllChangedFileSummaries,
                    pullRequest.ExistingThreads ?? [],
                    pullRequest.Title,
                    pullRequest.SourceBranch,
                    pullRequest.TargetBranch)
                : ReviewPrompts.BuildUserMessage(pullRequest);
        }
        else
        {
            // Whole-PR review path (default)
            state.Messages.Add(new ChatMessage(ChatRole.System, ReviewPrompts.BuildSystemPrompt(systemContext)));
            userMessage = ReviewPrompts.BuildUserMessage(pullRequest);
        }

        state.Messages.Add(new ChatMessage(ChatRole.User, userMessage));

        var registeredTools = BuildTools(systemContext.ReviewTools, cancellationToken);
        var effectiveModelId = systemContext.ModelId ?? opts.ModelId;
        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = 8192,
            ModelId = effectiveModelId,
            Tools = registeredTools.Count > 0 ? [.. registeredTools] : null,
        };

        var lastTextResponse = "";

        // T037: per-file override wins over the global option
        var effectiveMaxIterations = systemContext.PerFileHint?.MaxIterationsOverride ?? opts.MaxIterations;
        if (systemContext.PerFileHint?.MaxIterationsOverride is { } overrideVal)
        {
            LogMaxIterationsOverride(logger, overrideVal, systemContext.PerFileHint.FilePath, pullRequest.PullRequestId);
        }

        // T044: use tier-specific client when configured; fall back to injected default
        var effectiveClient = systemContext.TierChatClient ?? chatClient
            ?? throw new InvalidOperationException("No chat client available for review execution.");

        using var activity = ActivitySource.StartActivity("ReviewLoop");
        activity?.SetTag("pr.id", pullRequest.PullRequestId);
        activity?.SetTag("pr.iteration", pullRequest.IterationId);

        LogReviewLoopStarted(logger, pullRequest.PullRequestId, pullRequest.IterationId, effectiveMaxIterations);

        try
        {
            while (state.Iteration <= effectiveMaxIterations)
            {
                LogIterationStarted(logger, state.Iteration, effectiveMaxIterations);

                // Capture input sample BEFORE AddRange so we get the last message that was sent to the AI.
                // Tool result messages have FunctionResultContent (no .Text), so serialize them explicitly.
                var inputSample = GetInputSample(state.Messages);

                // On iterations 2+, exclude the global persona system message (index 0) from the per-file
                // review path to avoid retransmitting the large static prompt on every loop turn.
                var messagesToSend = state.Iteration > 1 && systemContext.PerFileHint is not null
                    ? (IList<ChatMessage>)state.Messages.Skip(1).ToList()
                    : state.Messages;

                var response = await effectiveClient.GetResponseAsync(messagesToSend, chatOptions, cancellationToken);
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
                                state.Iteration,
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
                state.Messages.Add(
                    new ChatMessage(
                        ChatRole.User,
                        "Iteration limit reached. Provide your final review now. " +
                        "CRITICAL: Your entire response must be a single raw JSON object with keys: " +
                        "summary, comments, confidence_evaluations, loop_complete (set to true), investigation_complete (set to true). " +
                        "Do NOT use markdown code fences. Do NOT add any text outside the JSON. " +
                        "The response must start with '{' and end with '}'."));
                var finalOptions = new ChatOptions { MaxOutputTokens = chatOptions.MaxOutputTokens, ModelId = effectiveModelId };
                var finalResponse = await effectiveClient.GetResponseAsync(state.Messages, finalOptions, cancellationToken);
                state.AccumulateTokens(finalResponse.Usage?.InputTokenCount, finalResponse.Usage?.OutputTokenCount);
                lastTextResponse = finalResponse.Text ?? "";

                if (string.IsNullOrWhiteSpace(lastTextResponse))
                {
                    throw new InvalidOperationException(
                        $"AI returned an empty response for the forced final review of PR {pullRequest.PullRequestId}. " +
                        "Review job cannot be completed.");
                }
            }

            // Schema-correction pass: if the model responded with content but omitted the
            // 'comments' key entirely (null = key absent = wrong schema invented by the model),
            // inject one corrective prompt so it reformats into the required structure.
            // 'Comments == null' distinguishes wrong-schema from a legitimate empty array,
            // but only fire for non-trivial responses (> 200 chars) to avoid false positives.
            if (ResponseNeedsSchemaCorrection(lastTextResponse))
            {
                LogForcingSchemaCorrection(logger, pullRequest.PullRequestId, state.Iteration);
                state.Messages.Add(new ChatMessage(ChatRole.Assistant, lastTextResponse));
                state.Messages.Add(new ChatMessage(
                    ChatRole.User,
                    "Your previous response used the wrong output schema. You MUST reformat it now. " +
                    "Output a single raw JSON object with EXACTLY these keys: " +
                    "\"summary\" (plain string), " +
                    "\"comments\" (array — move ALL review findings here as {\"file_path\": \"...\", \"line_number\": <int|null>, \"severity\": \"info\"|\"warning\"|\"error\"|\"suggestion\", \"message\": \"...\"}), " +
                    "\"confidence_evaluations\" (array), " +
                    "\"investigation_complete\": true, " +
                    "\"loop_complete\": true. " +
                    "The response must start with '{' and end with '}'. No markdown fences. No other keys."));
                var correctionOptions = new ChatOptions { MaxOutputTokens = chatOptions.MaxOutputTokens, ModelId = effectiveModelId };
                var correctionResponse = await effectiveClient.GetResponseAsync(state.Messages, correctionOptions, cancellationToken);
                state.AccumulateTokens(correctionResponse.Usage?.InputTokenCount, correctionResponse.Usage?.OutputTokenCount);
                var corrected = correctionResponse.Text ?? "";
                if (!string.IsNullOrWhiteSpace(corrected))
                {
                    lastTextResponse = corrected;
                }
            }

            activity?.SetTag("loop.iterations", state.Iteration);
            activity?.SetTag("loop.tool_calls", state.ToolCallCount);

            LogReviewLoopFinished(logger, pullRequest.PullRequestId, state.Iteration, state.ToolCallCount);

            systemContext.LoopMetrics = BuildLoopMetrics(state);

            return ParseReviewResult(lastTextResponse);
        }
        finally
        {
            // Ensure metrics are always captured so callers can record accurate stats even on failure.
            systemContext.LoopMetrics ??= BuildLoopMetrics(state);
        }
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
                    "Get the content of a file at a specific line range (1-based, inclusive). Use this to read full file contents when you only have a partial diff. Always use the PR source branch (shown in the per-file header) — never main or master.",
            });

        var askProCursorKnowledge = AIFunctionFactory.Create(
            (string question) => reviewTools.AskProCursorKnowledgeAsync(question, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "ask_procursor_knowledge",
                Description =
                    "Ask ProCursor a repository-aware knowledge question using the current review repository context. Returns sourced results with freshness metadata or an explicit no-result/unavailable status.",
            });

        var getProCursorSymbolInfo = AIFunctionFactory.Create(
            (string symbol, string? queryMode, int? maxRelations) =>
                reviewTools.GetProCursorSymbolInfoAsync(symbol, queryMode, maxRelations, cancellationToken),
            new AIFunctionFactoryOptions
            {
                Name = "get_procursor_symbol_info",
                Description =
                    "Ask ProCursor for symbol-aware insight using the current review repository context. Returns definitions plus related references, calls, inheritance, or containment when available.",
            });

        return [getChangedFiles, getFileTree, getFileContent, askProCursorKnowledge, getProCursorSymbolInfo];
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
        if (string.IsNullOrWhiteSpace(json) || json.Length <= 20)
        {
            return false;
        }

        var trimmed = json.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }
            else
            {
                // Single-line fence (no newline): skip opening ``` and optional language tag
                // by finding the first '{' that starts the JSON payload.
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

            trimmed = trimmed.Trim();
        }

        try
        {
            var dto = JsonSerializer.Deserialize<AgenticResponseDto>(trimmed, JsonOptions);
            if (dto is null || dto.Comments is not null)
            {
                // Either unparseable or Comments was present (possibly empty — that's valid).
                return false;
            }

            // Secondary guard: scan the raw JSON for any "comments"-named key (case-insensitive).
            // This catches alternate casings or key names that the DTO normaliser already handles,
            // preventing a spurious schema-correction call when the field is genuinely present.
            using var doc = JsonDocument.Parse(trimmed);
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

        // Strip markdown code fences the model sometimes wraps around its JSON response.
        // Handles ```json ... ``` and plain ``` ... ``` variants, including single-line fences.
        var trimmed = json.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }
            else
            {
                // Single-line fence: skip the opening ``` and optional language tag
                // by finding the first '{' that begins the JSON payload.
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

            json = trimmed.Trim();
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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Agentic review for PR#{PrId} at iteration {Iteration} returned wrong schema (comments key absent) — injecting schema-correction call")]
    private static partial void LogForcingSchemaCorrection(ILogger logger, int prId, int iteration);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Using MaxIterationsOverride={MaxIterationsOverride} for file {FilePath} (PR#{PrId})")]
    private static partial void LogMaxIterationsOverride(ILogger logger, int maxIterationsOverride, string filePath, int prId);

    private sealed record AgenticResponseDto(
        [property: JsonConverter(typeof(FlexibleStringConverter))] string? Summary,
        List<ReviewCommentDto>? Comments,
        [property: JsonConverter(typeof(LenientConfidenceListConverter))] List<ConfidenceEvaluationDto>? ConfidenceEvaluations,
        bool LoopComplete,
        bool? InvestigationComplete);  // null = absent = treated as complete (backward compat)

    private sealed record ReviewCommentDto(string? FilePath, int? LineNumber, string? Severity, string? Message);

    private sealed record ConfidenceEvaluationDto(
        string? Concern,
        [property: JsonConverter(typeof(FlexibleIntConverter))] int Confidence);

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

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(value);
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
            JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, options);
    }
}

