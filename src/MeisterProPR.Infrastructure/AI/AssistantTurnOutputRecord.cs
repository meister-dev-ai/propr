// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Builds the structured, write-time-bounded record persisted as the recorded output for one assistant turn of
///     an agentic review. The record captures the assistant's verbatim text, its reasoning (optional and bounded),
///     and each tool call as <c>{name, arguments}</c> — replacing the older lossy sample that kept either the text
///     or a bare tool-name list and dropped reasoning and call arguments entirely.
/// </summary>
/// <remarks>
///     The record is serialized to compact JSON. It MUST be bounded here, at write time: the trace reader leaves
///     JSON bodies whole (they double as structured panels), so an unbounded structured body would re-introduce
///     the memory blow-up the reader's free-text truncation was added to prevent. The reasoning field carries its
///     own dedicated cap; assistant text is the final review JSON (already bounded by the model's output limit) and
///     tool arguments are small.
/// </remarks>
internal static class AssistantTurnOutputRecord
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    ///     Builds the compact JSON record for an assistant turn, or <see langword="null" /> when the message carries
    ///     no text, reasoning, or tool calls (matching the previous "no sample" behaviour).
    /// </summary>
    /// <param name="message">The assistant response message for this turn.</param>
    /// <param name="captureReasoning">Whether reasoning content is captured (data-retention gate).</param>
    /// <param name="maxReasoningChars">Write-time cap on retained reasoning characters.</param>
    public static string? Build(ChatMessage? message, bool captureReasoning, int maxReasoningChars)
    {
        if (message is null)
        {
            return null;
        }

        var assistantText = string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));

        var reasoning = captureReasoning
            ? Bound(
                string.Concat(message.Contents.OfType<TextReasoningContent>().Select(content => content.Text)),
                maxReasoningChars)
            : null;

        var toolCalls = message.Contents
            .OfType<FunctionCallContent>()
            .Select(call => new ToolCallRecord(call.Name ?? string.Empty, call.Arguments))
            .ToList();

        if (string.IsNullOrEmpty(assistantText) && string.IsNullOrEmpty(reasoning) && toolCalls.Count == 0)
        {
            return null;
        }

        var record = new AssistantTurnRecord(
            assistantText,
            string.IsNullOrEmpty(reasoning) ? null : reasoning,
            toolCalls.Count > 0 ? toolCalls : null);

        return JsonSerializer.Serialize(record, SerializerOptions);
    }

    private static string? Bound(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
        {
            return text;
        }

        // A zero or negative cap keeps nothing; return empty so the caller omits the field rather than indexing
        // into the string with a non-positive cut position.
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        var cut = maxChars;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        var omitted = text.Length - cut;
        return text[..cut] + $"… [reasoning truncated, {omitted} chars omitted]";
    }

    // The "assistantText" property is always emitted (even when empty) so the frontend can reliably distinguish
    // this structured envelope from a bare final-review JSON body.
    private sealed record AssistantTurnRecord(
        [property: JsonPropertyName("assistantText")]
        string AssistantText,
        [property: JsonPropertyName("reasoning")]
        string? Reasoning,
        [property: JsonPropertyName("toolCalls")]
        IReadOnlyList<ToolCallRecord>? ToolCalls);

    private sealed record ToolCallRecord(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")]
        IDictionary<string, object?>? Arguments);
}
