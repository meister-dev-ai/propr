// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Carries a reasoning model's own chain of thought back to it. Some providers — DeepSeek most visibly —
///     return a non-standard <c>reasoning_content</c> field alongside an assistant message and then require it
///     verbatim on that turn in the next request, answering <c>400 The reasoning_content in the thinking mode
///     must be passed back to the API</c> when it is missing.
/// </summary>
/// <remarks>
///     <para>
///         This has to live at the transport, and that was established by measurement rather than preference:
///         neither reasoning content nor additional properties on an assistant message survive the client
///         library's serialization, so nothing above the wire can put the field back. The handler therefore reads
///         and rewrites JSON bodies directly.
///     </para>
///     <para>
///         It configures itself from the wire rather than from model metadata. A provider that never sends the
///         field causes nothing to be remembered and nothing to be injected, so the OpenAI and Azure paths pay
///         only a substring check per response.
///     </para>
///     <para>
///         Streaming responses are not handled: the field arrives split across server-sent events, and the review
///         loop does not stream. A streaming caller against such a model would still hit the provider's 400.
///     </para>
/// </remarks>
public sealed class ReasoningContentRoundTripHandler : DelegatingHandler
{
    /// <summary>The response field this handler round-trips.</summary>
    public const string ReasoningContentField = "reasoning_content";

    private readonly ReasoningContentMemory _memory = new();

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await this.ReinjectAsync(request, cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await this.RememberAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    ///     Builds the key that identifies an assistant turn across a request and the response that produced it:
    ///     its tool-call ids when it made any, otherwise its exact text.
    /// </summary>
    private static string? BuildKey(JsonNode? toolCalls, string? content)
    {
        if (toolCalls is JsonArray { Count: > 0 } calls)
        {
            var ids = calls
                .Select(call => call?["id"]?.GetValue<string>())
                .OfType<string>()
                .ToList();

            if (ids.Count > 0)
            {
                return "tools:" + string.Join(",", ids);
            }
        }

        return string.IsNullOrEmpty(content) ? null : "text:" + content;
    }

    private static string? ReadString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private async Task RememberAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content is null || !LooksLikeJson(response.Content.Headers.ContentType))
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!body.Contains(ReasoningContentField, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(body) is not JsonObject payload || payload["choices"] is not JsonArray choices)
            {
                return;
            }

            foreach (var choice in choices)
            {
                if (choice?["message"] is not JsonObject message)
                {
                    continue;
                }

                var reasoning = ReadString(message[ReasoningContentField]);
                if (string.IsNullOrEmpty(reasoning))
                {
                    continue;
                }

                this._memory.Remember(BuildKey(message["tool_calls"], ReadString(message["content"])), reasoning);
            }
        }
        catch (JsonException)
        {
            // A body that says it is JSON and is not is the provider's problem, not this handler's; the caller
            // will fail on it in a way that names the provider.
            return;
        }

        // The body was read to a string, so hand the response a fresh, replayable copy of it.
        var replacement = new StringContent(body, Encoding.UTF8);
        CopyHeaders(response.Content.Headers, replacement.Headers);
        response.Content.Dispose();
        response.Content = replacement;
    }

    private async Task ReinjectAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (this._memory.IsEmpty || request.Content is null || !LooksLikeJson(request.Content.Headers.ContentType))
        {
            return;
        }

        var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!body.Contains("\"messages\"", StringComparison.Ordinal))
        {
            return;
        }

        JsonObject payload;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject parsed || parsed["messages"] is not JsonArray messages)
            {
                return;
            }

            payload = parsed;
            if (!Reinject(messages, this._memory))
            {
                return;
            }
        }
        catch (JsonException)
        {
            return;
        }

        var replacement = new StringContent(payload.ToJsonString(), Encoding.UTF8);
        CopyHeaders(request.Content.Headers, replacement.Headers);
        request.Content.Dispose();
        request.Content = replacement;
    }

    private static bool Reinject(JsonArray messages, ReasoningContentMemory memory)
    {
        var injected = false;

        foreach (var entry in messages)
        {
            if (entry is not JsonObject message
                || !string.Equals(ReadString(message["role"]), "assistant", StringComparison.Ordinal)
                || message[ReasoningContentField] is not null)
            {
                continue;
            }

            var reasoning = memory.Recall(BuildKey(message["tool_calls"], ReadString(message["content"])));
            if (reasoning is null)
            {
                continue;
            }

            message[ReasoningContentField] = reasoning;
            injected = true;
        }

        return injected;
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
            // wrong length the moment a field was added.
            if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            destination.Remove(header.Key);
            destination.TryAddWithoutValidation(header.Key, header.Value);
        }
    }
}
