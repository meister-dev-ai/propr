// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Runs the evidence judge as a bounded tool loop. The judge may call read-only repository lookups, the same tool
///     functions the reviewing passes use, at most <see cref="MaxToolCallsPerClaim" /> times per claim. After the last
///     allowed call the judge gets one more turn in which it must answer: the request still declares the tools, because
///     some providers refuse a conversation with tool calls and no tool definitions, and sets
///     <see cref="ChatToolMode.None" /> so the judge cannot call them. Each tool result is cut to
///     <see cref="MaxToolResultChars" /> characters before the judge receives it. A failed model call is raised as an
///     <see cref="EvidenceJudgeProviderException" />. Output exhaustion permits one larger verdict attempt, with
///     completed tool results retained and further lookups disabled, inside the caller's existing deadline.
/// </summary>
internal static class EvidenceJudgeToolLoop
{
    /// <summary>Maximum number of tool calls the judge may make for one claim.</summary>
    internal const int MaxToolCallsPerClaim = 4;

    /// <summary>Maximum number of characters of one tool result the judge receives.</summary>
    internal const int MaxToolResultChars = 6000;

    private static readonly HashSet<string> JudgeToolNames = new(StringComparer.Ordinal)
    {
        "get_file_content",
        BoundedReviewContextTools.SearchCodeToolName,
        BoundedReviewContextTools.SearchPathsToolName,
        "find_references",
        "get_definition",
    };

    /// <summary>The read-only lookups of the review tools that the judge may call.</summary>
    internal static IReadOnlyList<AIFunction> BuildJudgeTools(IReviewContextTools tools, CancellationToken ct)
    {
        return ToolAwareAiReviewCore.BuildTools(tools, true, false, _ => { }, ct)
            .Where(tool => JudgeToolNames.Contains(tool.Name))
            .ToList();
    }

    /// <summary>
    ///     Runs the loop and returns the final response with its output-limit status. <paramref name="onModelCall" />
    ///     receives every response and whether it finishes an attempt without executing tools;
    ///     <paramref name="onToolCall" /> receives every executed lookup.
    /// </summary>
    internal static async Task<JudgeLoopResult> RunAsync(
        IChatClient client,
        ChatOptions baseOptions,
        int retryOutputTokens,
        Func<ChatResponse, bool> hasUsableVerdict,
        string systemPrompt,
        string userMessage,
        IReadOnlyList<AIFunction> tools,
        Func<ChatResponse, bool, Task> onModelCall,
        Func<JudgeToolCall, Task> onToolCall,
        Func<JudgeOutputLimit, Task> onOutputLimit,
        CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userMessage),
        };
        var toolsByName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var used = 0;
        var retried = false;
        var modelTurn = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var allowTools = !retried && used < MaxToolCallsPerClaim && tools.Count > 0;
            var options = baseOptions.Clone();
            options.MaxOutputTokens = retried ? retryOutputTokens : baseOptions.MaxOutputTokens;
            options.Tools = tools.Count > 0 ? [.. tools] : null;
            options.ToolMode = tools.Count > 0 && !allowTools ? ChatToolMode.None : null;
            var response = await GetResponseAsync(client, messages, options, ct).ConfigureAwait(false);
            modelTurn++;
            var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToList();
            if (IsOutputLimited(response, options.MaxOutputTokens!.Value, (calls.Count == 0 || !allowTools) && !hasUsableVerdict(response)))
            {
                await onModelCall(response, true).ConfigureAwait(false);
                await onOutputLimit(new JudgeOutputLimit(modelTurn, options.MaxOutputTokens.Value, response, !retried))
                    .ConfigureAwait(false);
                if (retried)
                {
                    return new JudgeLoopResult(response, true, used);
                }

                // Retry from the complete conversation. Partial output is not evidence or a completed tool request.
                retried = true;
                continue;
            }

            if (calls.Count == 0 || !allowTools || HasErrorOrRefusal(response))
            {
                await onModelCall(response, true).ConfigureAwait(false);
                return new JudgeLoopResult(response, false, used);
            }

            await onModelCall(response, false).ConfigureAwait(false);
            messages.AddRange(response.Messages);
            var results = new List<AIContent>(calls.Count);
            foreach (var call in calls)
            {
                if (used >= MaxToolCallsPerClaim)
                {
                    results.Add(new FunctionResultContent(call.CallId, """{"status":"not_run","reason":"tool_call_limit_reached"}"""));
                    continue;
                }

                used++;
                var (text, originalLength, succeeded) = await InvokeAsync(toolsByName, call, ct).ConfigureAwait(false);
                results.Add(new FunctionResultContent(call.CallId, text));
                await onToolCall(
                        new JudgeToolCall(call.Name, SerializeArguments(call.Arguments), originalLength, originalLength > MaxToolResultChars, text, succeeded))
                    .ConfigureAwait(false);
            }

            messages.Add(new ChatMessage(ChatRole.Tool, results));
        }
    }

    private static bool IsOutputLimited(ChatResponse response, int requestedTokens, bool unusableVerdict)
    {
        if (HasErrorOrRefusal(response))
        {
            return false;
        }

        return response.FinishReason == ChatFinishReason.Length
               || (unusableVerdict && response.Usage?.OutputTokenCount >= requestedTokens);
    }

    private static bool HasErrorOrRefusal(ChatResponse response)
    {
        return response.FinishReason == ChatFinishReason.ContentFilter
               || response.Messages.SelectMany(message => message.Contents).OfType<ErrorContent>().Any();
    }

    // A failure of the model call itself is raised as a provider failure, so the verifier can tell it from a failure
    // of its own steps. A cancellation that the caller did not request, such as an HTTP client timeout, is a provider
    // failure as well.
    private static async Task<ChatResponse> GetResponseAsync(
        IChatClient client,
        List<ChatMessage> messages,
        ChatOptions options,
        CancellationToken ct)
    {
        try
        {
            return await client.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EvidenceJudgeProviderException(ex);
        }
    }

    private static async Task<(string Text, int OriginalLength, bool Succeeded)> InvokeAsync(
        IReadOnlyDictionary<string, AIFunction> toolsByName,
        FunctionCallContent call,
        CancellationToken ct)
    {
        string text;
        var succeeded = false;
        if (!toolsByName.TryGetValue(call.Name, out var tool))
        {
            text = JsonSerializer.Serialize(new { status = "not_run", reason = "unknown_tool", tool = call.Name });
        }
        else
        {
            try
            {
                var result = await tool.InvokeAsync(new AIFunctionArguments(call.Arguments), ct).ConfigureAwait(false);
                text = result as string ?? JsonSerializer.Serialize(result);
                succeeded = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A failed lookup, including one cancelled by its own timeout, is reported to the judge as data, so it can
                // answer from the other inputs.
                text = JsonSerializer.Serialize(new { status = "failed", error = ex.GetType().Name, message = ex.Message });
            }
        }

        var originalLength = text.Length;
        return originalLength <= MaxToolResultChars
            ? (text, originalLength, succeeded)
            : (string.Concat(text.AsSpan(0, MaxToolResultChars), EvidenceJudgeInput.TruncationMarker), originalLength, succeeded);
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        var text = arguments is null ? "{}" : JsonSerializer.Serialize(arguments);
        return text.Length <= 500 ? text : text[..500];
    }
}

/// <summary>The final response, whether the judge exhausted its bounded output recovery, and the lookups it ran.</summary>
/// <param name="Response">The last model response.</param>
/// <param name="OutputLimitReached">Whether the final attempt exhausted its output budget.</param>
/// <param name="ToolCallsUsed">The number of lookups that ran for the claim.</param>
internal sealed record JudgeLoopResult(ChatResponse Response, bool OutputLimitReached, int ToolCallsUsed)
{
    /// <summary>
    ///     Whether the judge ran every lookup it was allowed. Its answer then came from the forced final turn, after
    ///     which no further code could be read.
    /// </summary>
    public bool UsedEveryLookup => this.ToolCallsUsed >= EvidenceJudgeToolLoop.MaxToolCallsPerClaim;
}

/// <summary>A capped model turn and the decision to retry it.</summary>
/// <param name="ModelTurn">The model-turn number within the claim.</param>
/// <param name="MaxOutputTokens">The requested output ceiling for this turn.</param>
/// <param name="Response">The capped response, including its measured usage.</param>
/// <param name="RetryScheduled">Whether the claim has its one budget retry available.</param>
internal sealed record JudgeOutputLimit(int ModelTurn, int MaxOutputTokens, ChatResponse Response, bool RetryScheduled);

/// <summary>One tool call of the evidence judge, as the protocol records it.</summary>
/// <param name="ToolName">The tool the judge called.</param>
/// <param name="Arguments">The call arguments as JSON, cut to 500 characters.</param>
/// <param name="ResultChars">The length of the tool result before it was cut.</param>
/// <param name="ResultTruncated">Whether the judge received a cut result.</param>
internal sealed record JudgeToolCall(
    string ToolName,
    string Arguments,
    int ResultChars,
    bool ResultTruncated,
    string? ResultText = null,
    bool Succeeded = false);

/// <summary>The model call of the evidence judge failed at the provider.</summary>
/// <param name="inner">The exception the chat client raised.</param>
internal sealed class EvidenceJudgeProviderException(Exception inner)
    : Exception($"{inner.GetType().Name}: {inner.Message}", inner);
