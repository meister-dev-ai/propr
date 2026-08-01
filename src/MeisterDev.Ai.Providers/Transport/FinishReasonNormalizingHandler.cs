// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Repairs a chat completion whose <c>finish_reason</c> the OpenAI client library refuses to read. That type is
///     a closed enum over <c>stop</c>, <c>length</c>, <c>content_filter</c>, <c>tool_calls</c>, and
///     <c>function_call</c>; anything else throws <see cref="ArgumentOutOfRangeException" /> while the response is
///     still being deserialized, so the call fails before the caller ever sees the message the model produced.
/// </summary>
/// <remarks>
///     <para>
///         Two deviations reach this handler. An OpenAI-compatible server may send an explicit <c>null</c>, which
///         the specification does not permit on a non-streaming completion, and a gateway fronting another vendor
///         may forward that vendor's own vocabulary instead of translating it, of which <c>end_turn</c> and
///         <c>tool_use</c> are the ones seen in practice. Both abort the same way.
///     </para>
///     <para>
///         Like the reasoning round-trip beside it, this has to sit at the transport: the failure happens inside the
///         client library's deserializer, so nothing above the wire is ever handed a response to correct. It
///         configures itself from the body rather than from provider metadata, so a conforming provider pays one
///         substring check per response and is otherwise untouched.
///     </para>
///     <para>
///         Streaming responses are deliberately left alone. A <c>null</c> finish reason is legal, and usual, on every
///         chunk of a stream but the last, so rewriting one there would invent a completion the model never signalled.
///         They are excluded by content type, because a stream is not served as JSON.
///     </para>
/// </remarks>
public sealed class FinishReasonNormalizingHandler : DelegatingHandler
{
    /// <summary>The response field this handler repairs.</summary>
    public const string FinishReasonField = "finish_reason";

    /// <summary>The only values the client library's enum accepts. It compares them case-insensitively.</summary>
    private static readonly HashSet<string> Readable = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop",
        "length",
        "content_filter",
        "tool_calls",
        "function_call",
    };

    /// <summary>
    ///     Another vendor's vocabulary, mapped to the OpenAI term with the same meaning. Only values whose intent is
    ///     unambiguous are listed; anything absent is inferred from the message instead, which is the safer default
    ///     because it reads what the model actually returned rather than trusting a word this table guessed at.
    /// </summary>
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["end_turn"] = "stop",
        ["stop_sequence"] = "stop",
        ["eos"] = "stop",
        ["max_tokens"] = "length",
        ["tool_use"] = "tool_calls",
        ["refusal"] = "content_filter",
    };

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await NormalizeAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static async Task NormalizeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null || !LooksLikeJson(response.Content.Headers.ContentType))
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!body.Contains(FinishReasonField, StringComparison.Ordinal))
        {
            return;
        }

        JsonObject payload;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject parsed || parsed["choices"] is not JsonArray choices)
            {
                return;
            }

            payload = parsed;
            if (!Repair(choices))
            {
                return;
            }
        }
        catch (JsonException)
        {
            // A body that says it is JSON and is not is the provider's problem, not this handler's; the caller
            // will fail on it in a way that names the provider.
            return;
        }

        var replacement = new StringContent(payload.ToJsonString(), Encoding.UTF8);
        CopyHeaders(response.Content.Headers, replacement.Headers);
        response.Content.Dispose();
        response.Content = replacement;
    }

    /// <summary>Rewrites every unreadable finish reason in <paramref name="choices" />, reporting whether any changed.</summary>
    private static bool Repair(JsonArray choices)
    {
        var repaired = false;

        foreach (var entry in choices)
        {
            // An absent field is left absent. The client library treats that as a normal stop, so writing one in
            // would be a change with no reader, and this handler only exists to prevent a throw.
            if (entry is not JsonObject choice || !choice.ContainsKey(FinishReasonField))
            {
                continue;
            }

            var current = ReadString(choice[FinishReasonField]);
            if (current is not null && Readable.Contains(current))
            {
                continue;
            }

            choice[FinishReasonField] = Resolve(current, choice["message"] as JsonObject);
            repaired = true;
        }

        return repaired;
    }

    /// <summary>
    ///     Decides what the provider meant. A synonym is taken at its word; otherwise the message itself decides,
    ///     because an assistant turn carrying tool calls has by definition stopped in order to make them.
    /// </summary>
    private static string Resolve(string? reported, JsonObject? message)
    {
        if (reported is not null && Synonyms.TryGetValue(reported, out var synonym))
        {
            return synonym;
        }

        return message?["tool_calls"] is JsonArray { Count: > 0 } ? "tool_calls" : "stop";
    }

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static bool LooksLikeJson(MediaTypeHeaderValue? contentType)
    {
        return contentType?.MediaType is { } mediaType
               && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);
    }

    private static void CopyHeaders(HttpContentHeaders source, HttpContentHeaders destination)
    {
        foreach (var header in source)
        {
            // Content-Length is recomputed for the replacement body; copying the old one would describe the
            // wrong length the moment a field changed width.
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            destination.Remove(header.Key);
            destination.TryAddWithoutValidation(header.Key, header.Value);
        }
    }
}
