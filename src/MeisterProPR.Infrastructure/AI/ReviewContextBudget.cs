// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     How the assembled review payload relates to the model's context-window budget.
/// </summary>
public enum ContextBudgetClassification
{
    /// <summary>The full payload fits the input budget; send it unchanged.</summary>
    WithinBudget,

    /// <summary>
    ///     The full payload is over budget, but the minimal payload (system prompt + diff) fits once the
    ///     droppable file-content windows are removed. The file is reviewed diff-only.
    /// </summary>
    DegradedDiffOnly,

    /// <summary>
    ///     Even the minimal payload (system prompt + diff) exceeds the input budget. The file cannot be
    ///     reviewed without overflowing the window and is skipped without a provider call.
    /// </summary>
    Skipped,
}

/// <summary>Outcome of pre-flight classification of the initial review payload.</summary>
/// <param name="Classification">Whether the payload fits, must be degraded, or must be skipped.</param>
/// <param name="EstimatedInputTokens">Estimated token count of the full assembled payload.</param>
/// <param name="InputBudgetTokens">The computed input budget the payload was measured against.</param>
/// <param name="MaxContextTokens">The effective model context-window size used for budgeting.</param>
public sealed record ContextBudgetPreflight(
    ContextBudgetClassification Classification,
    int EstimatedInputTokens,
    int InputBudgetTokens,
    int MaxContextTokens);

/// <summary>Outcome of trimming accumulated tool-result history to fit the input budget.</summary>
/// <param name="Messages">The (possibly compacted) message list to send.</param>
/// <param name="Trimmed">True when at least one tool-result message was compacted.</param>
/// <param name="EstimatedTokensBefore">Estimated tokens before trimming.</param>
/// <param name="EstimatedTokensAfter">Estimated tokens after trimming.</param>
/// <param name="CompactedMessageCount">Number of tool-result messages compacted.</param>
public sealed record ToolHistoryTrimResult(
    IReadOnlyList<ChatMessage> Messages,
    bool Trimmed,
    int EstimatedTokensBefore,
    int EstimatedTokensAfter,
    int CompactedMessageCount);

/// <summary>
///     Deterministic pre-flight token budgeting for the agentic review loop. Estimates prompt and context
///     tokens against a per-model context window and trims to fit — accumulated tool-result history first,
///     then the file-content windows — so a review is degraded or skipped rather than sent to the provider
///     with a context that is too large.
/// </summary>
public static class ReviewContextBudget
{
    /// <summary>
    ///     Conservative built-in context window applied when a model has no configured
    ///     <c>MaxContextTokens</c>, so budgeting always has a number to work with.
    /// </summary>
    public const int DefaultMaxContextTokens = 128_000;

    /// <summary>
    ///     Tokens reserved on top of the model's output allowance for role framing, tool schema, and
    ///     estimator error that the text-only estimate cannot see.
    /// </summary>
    internal const int SafetyMarginTokens = 2_000;

    private const string CompactedToolResultPlaceholder =
        "[context-budget: earlier tool evidence omitted to fit the model context window. " +
        "Re-run the tool with a narrower range if this evidence is needed again.]";

    private const string CompactedAssistantPlaceholder =
        "[context-budget: an earlier reasoning turn was omitted to fit the model context window.]";

    /// <summary>Resolves the effective context window, applying the built-in default when unset or invalid.</summary>
    public static int ResolveMaxContextTokens(int? configured) =>
        configured is int value && value > 0 ? value : DefaultMaxContextTokens;

    /// <summary>
    ///     Computes the input-token budget for a call: the context window minus the reserved output
    ///     allowance and a safety margin, floored at 1.
    /// </summary>
    public static int ComputeInputBudget(int maxContextTokens, int reservedOutputTokens) =>
        Math.Max(1, maxContextTokens - Math.Max(0, reservedOutputTokens) - SafetyMarginTokens);

    /// <summary>
    ///     Estimates the token count of a text using the model's tokenizer when it maps to a supported
    ///     encoding, otherwise the codebase's char-per-token heuristic.
    /// </summary>
    public static int EstimateTokens(string? tokenizerName, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        if (EmbeddingTokenizerRegistry.IsSupported(tokenizerName))
        {
            try
            {
                return EmbeddingTokenizerRegistry.CountTokens(tokenizerName!, text);
            }
            catch (Exception)
            {
                // The registry advertises encodings (e.g. claude) that the underlying tokenizer rejects at
                // resolution time. Budgeting must never fail the review, so any tokenizer fault falls through
                // to the char-based heuristic below.
            }
        }

        return Math.Max(1, text.Length / 4);
    }

    /// <summary>Estimates the token count of a single chat message.</summary>
    public static int EstimateMessageTokens(string? tokenizerName, ChatMessage message) =>
        EstimateTokens(tokenizerName, GetMessageText(message));

    /// <summary>Estimates the total token count of an assembled message list.</summary>
    public static int EstimateMessagesTokens(string? tokenizerName, IEnumerable<ChatMessage> messages) =>
        messages.Sum(message => EstimateMessageTokens(tokenizerName, message));

    /// <summary>
    ///     Classifies the initial review payload against the input budget. The minimal payload is the first
    ///     system message (persona / system prompt) plus the user messages (the diff); additional system
    ///     messages carry the droppable file-content windows.
    /// </summary>
    public static ContextBudgetPreflight ClassifyInitialPayload(
        IReadOnlyList<ChatMessage> messages,
        string? tokenizerName,
        int inputBudget,
        int maxContextTokens)
    {
        var fullTokens = EstimateMessagesTokens(tokenizerName, messages);
        if (fullTokens <= inputBudget)
        {
            return new ContextBudgetPreflight(ContextBudgetClassification.WithinBudget, fullTokens, inputBudget, maxContextTokens);
        }

        var minimalTokens = EstimateMinimalPayloadTokens(messages, tokenizerName);
        var classification = minimalTokens <= inputBudget
            ? ContextBudgetClassification.DegradedDiffOnly
            : ContextBudgetClassification.Skipped;

        return new ContextBudgetPreflight(classification, fullTokens, inputBudget, maxContextTokens);
    }

    /// <summary>
    ///     Produces the diff-only message list: keeps the first system message and every non-system message,
    ///     dropping the additional system messages that carry file-content windows.
    /// </summary>
    public static List<ChatMessage> ToDiffOnly(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        var keptSystem = false;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (!keptSystem)
                {
                    result.Add(message);
                    keptSystem = true;
                }

                continue;
            }

            result.Add(message);
        }

        return result;
    }

    /// <summary>
    ///     Compacts accumulated history, oldest first, until the assembled list fits the input budget: first
    ///     tool-result messages (each keeps its call identity so request/result pairing survives, its payload
    ///     replaced with a short refreshable placeholder), then — only if still over budget — older plain-text
    ///     assistant turns, preserving the most recent assistant turn and any assistant message that carries a
    ///     function call.
    /// </summary>
    public static ToolHistoryTrimResult TrimToolHistoryToBudget(
        IReadOnlyList<ChatMessage> messages,
        string? tokenizerName,
        int inputBudget)
    {
        var (perMessageTokens, total) = MeasureMessageTokens(messages, tokenizerName);

        var originalTotal = total;
        if (total <= inputBudget)
        {
            return new ToolHistoryTrimResult(messages, false, originalTotal, originalTotal, 0);
        }

        var working = messages.ToList();
        var compacted = CompactToolResultsPass(working, perMessageTokens, tokenizerName, ref total, inputBudget);

        // Fallback: if compacting every tool result was not enough, compact older plain-text assistant turns
        // (oldest-first). The most recent assistant turn and any assistant message carrying a function call are
        // preserved so tool-call/result pairing and the latest reasoning survive.
        if (total > inputBudget)
        {
            compacted += CompactOlderAssistantPass(working, perMessageTokens, tokenizerName, ref total, inputBudget);
        }

        return new ToolHistoryTrimResult(working, compacted > 0, originalTotal, total, compacted);
    }

    private static (int[] PerMessageTokens, int Total) MeasureMessageTokens(
        IReadOnlyList<ChatMessage> messages,
        string? tokenizerName)
    {
        var perMessageTokens = new int[messages.Count];
        var total = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            perMessageTokens[i] = EstimateMessageTokens(tokenizerName, messages[i]);
            total += perMessageTokens[i];
        }

        return (perMessageTokens, total);
    }

    private static int CompactToolResultsPass(
        List<ChatMessage> working,
        int[] perMessageTokens,
        string? tokenizerName,
        ref int total,
        int inputBudget)
    {
        var compacted = 0;
        for (var i = 0; i < working.Count && total > inputBudget; i++)
        {
            var message = working[i];
            if (message.Role != ChatRole.Tool || IsAlreadyCompacted(message))
            {
                continue;
            }

            var compactedMessage = CompactToolMessage(message);
            var newTokens = EstimateMessageTokens(tokenizerName, compactedMessage);
            total += newTokens - perMessageTokens[i];
            perMessageTokens[i] = newTokens;
            working[i] = compactedMessage;
            compacted++;
        }

        return compacted;
    }

    private static int CompactOlderAssistantPass(
        List<ChatMessage> working,
        int[] perMessageTokens,
        string? tokenizerName,
        ref int total,
        int inputBudget)
    {
        var lastAssistantIndex = FindLastAssistantIndex(working);

        var compacted = 0;
        for (var i = 0; i < working.Count && total > inputBudget; i++)
        {
            if (i == lastAssistantIndex)
            {
                continue;
            }

            var message = working[i];
            if (message.Role != ChatRole.Assistant
                || message.Contents.OfType<FunctionCallContent>().Any()
                || string.IsNullOrEmpty(message.Text)
                || message.Text == CompactedAssistantPlaceholder)
            {
                continue;
            }

            var compactedMessage = new ChatMessage(ChatRole.Assistant, CompactedAssistantPlaceholder);
            var newTokens = EstimateMessageTokens(tokenizerName, compactedMessage);
            total += newTokens - perMessageTokens[i];
            perMessageTokens[i] = newTokens;
            working[i] = compactedMessage;
            compacted++;
        }

        return compacted;
    }

    private static int FindLastAssistantIndex(IReadOnlyList<ChatMessage> working)
    {
        for (var i = working.Count - 1; i >= 0; i--)
        {
            if (working[i].Role == ChatRole.Assistant)
            {
                return i;
            }
        }

        return -1;
    }

    private static int EstimateMinimalPayloadTokens(IReadOnlyList<ChatMessage> messages, string? tokenizerName)
    {
        var total = 0;
        var sawSystem = false;
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.System)
            {
                if (!sawSystem)
                {
                    total += EstimateMessageTokens(tokenizerName, message);
                    sawSystem = true;
                }

                continue;
            }

            if (message.Role == ChatRole.User)
            {
                total += EstimateMessageTokens(tokenizerName, message);
            }
        }

        return total;
    }

    private static bool IsAlreadyCompacted(ChatMessage message) =>
        message.Contents.OfType<FunctionResultContent>().Any() &&
        message.Contents.OfType<FunctionResultContent>()
            .All(content => (content.Result as string) == CompactedToolResultPlaceholder);

    private static ChatMessage CompactToolMessage(ChatMessage message)
    {
        var newContents = new List<AIContent>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            newContents.Add(
                content is FunctionResultContent result
                    ? new FunctionResultContent(result.CallId, CompactedToolResultPlaceholder)
                    : content);
        }

        return new ChatMessage(ChatRole.Tool, newContents);
    }

    private static string GetMessageText(ChatMessage message)
    {
        if (!string.IsNullOrEmpty(message.Text))
        {
            return message.Text;
        }

        // Tool and assistant tool-call messages carry no plain text; approximate their size from the
        // function-result payloads and call names that actually consume context.
        var builder = new StringBuilder();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case FunctionResultContent result:
                    builder.Append(result.Result as string ?? result.Result?.ToString());
                    break;
                case FunctionCallContent call:
                    builder.Append(call.Name);
                    break;
            }
        }

        return builder.ToString();
    }
}
