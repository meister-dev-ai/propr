// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Transport;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.AnthropicAddIn;

/// <summary>
///     Speaks Anthropic's native Messages API as an <see cref="IChatClient" />.
/// </summary>
/// <remarks>
///     <para>
///         Written against the protocol rather than wrapped around a client library because Anthropic ships no
///         first-party .NET SDK, and the things the native class exists for — cache-control breakpoints, the
///         thinking block, its own usage counters — are exactly the details a third-party wrapper is slowest to
///         expose. Depending on one would put this family's roadmap behind someone else's.
///     </para>
///     <para>
///         Three shape differences from the OpenAI family drive most of the translation: the system prompt is a
///         top-level field rather than a message, tool results are <em>user</em> messages carrying a
///         <c>tool_result</c> block rather than a role of their own, and <c>max_tokens</c> is required.
///     </para>
/// </remarks>
public sealed class AnthropicMessagesChatClient : INativeProtocolChatClient
{
    /// <summary>The API version this client is written against, sent on every request as Anthropic requires.</summary>
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>The header carrying the API version, which Anthropic requires on every request.</summary>
    public const string VersionHeaderName = "anthropic-version";

    /// <summary>
    ///     The header Anthropic reads its API key from. A bearer token is rejected, so this is where the
    ///     credential goes whichever authentication mode the profile was saved under.
    /// </summary>
    public const string ApiKeyHeaderName = "x-api-key";

    /// <summary>Anthropic requires an output cap, so one is sent when the caller expresses no preference.</summary>
    private const int DefaultMaxTokens = 8192;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IProviderHttpClientFactory? _http;
    private readonly ProviderEndpoint _endpoint;
    private readonly ProviderModelDescriptor _model;

    /// <summary>Initializes a new instance of the <see cref="AnthropicMessagesChatClient" /> class.</summary>
    /// <param name="http">
    ///     The host's client factory, from which the egress-guarded client runtime traffic goes through is taken,
    ///     or null where the host supplied none.
    /// </param>
    /// <param name="endpoint">Where to reach the provider and how to authenticate.</param>
    /// <param name="model">The model this client is bound to.</param>
    /// <remarks>
    ///     The factory is held rather than a client, and the client is taken on the first call. A host builds one
    ///     of these to describe a family as well as to call it — the shared driver checks construct one against an
    ///     endpoint that belongs to no connection — and a client taken in the constructor would make describing
    ///     the family fail for want of a factory nothing was going to use.
    /// </remarks>
    public AnthropicMessagesChatClient(
        IProviderHttpClientFactory? http,
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);

        this._http = http;
        this._endpoint = endpoint;
        this._model = model;
    }

    /// <inheritdoc />
    public string NativeProtocol => AnthropicProviderDriver.MessagesProtocol;

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var payload = this.BuildRequest([.. messages], options);
        using var request = this.CreateRequest(payload);
        // Taken per call. The factory hands back a thin client over the host's pooled handler chain, so holding
        // one saves nothing, and a field assigned with ??= is written by every call that finds it empty.
        using var client = AnthropicTransport.Client(this._http, ProviderHttpPurpose.Runtime);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        // Bounded by the same limit discovery reads under. A Messages response is one document and this family
        // does not stream, so nothing legitimate approaches it. A body that does is a transport fault, and
        // parsing the part that arrived would report a truncated answer as the model's own.
        var (body, truncated) = await ProviderResponseBody
            .ReadBoundedAsync(response.Content, ProviderResponseBody.MaximumDocumentBytes, cancellationToken)
            .ConfigureAwait(false);

        if (truncated)
        {
            throw new HttpRequestException(
                $"Anthropic's response is longer than the {ProviderResponseBody.MaximumDocumentBytes} bytes this "
                + "host reads from a response body.",
                null,
                HttpStatusCode.BadGateway);
        }

        if (!response.IsSuccessStatusCode)
        {
            // Thrown with the status attached so the shared classifier can decide whether it is worth retrying,
            // exactly as it does for every other provider.
            throw new HttpRequestException(
                $"Anthropic rejected the request: {DescribeError(body) ?? response.ReasonPhrase}",
                null,
                response.StatusCode);
        }

        return ParseResponse(body, this._model.RemoteModelId);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Streaming is not implemented: the review loop does not stream, and a half-built server-sent-event
    ///     reader that silently dropped tool calls would be worse than an honest refusal.
    /// </remarks>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("The Anthropic driver does not implement streaming responses. Use GetResponseAsync.");
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public TService? GetService<TService>(object? key = null)
        where TService : class => this.GetService(typeof(TService), key) as TService;

    /// <inheritdoc />
    /// <inheritdoc />
    /// <remarks>
    ///     Nothing to release. The client each call takes from the host's factory is disposed by that call, and
    ///     disposing one stops at the host's pooled handler chain, which the factory keeps and rotates on its own
    ///     schedule.
    /// </remarks>
    public void Dispose()
    {
    }

    /// <summary>
    ///     Applies the two things Anthropic requires of every request, discovery included: the credential in the
    ///     header it reads, and the API version.
    /// </summary>
    /// <remarks>
    ///     The operator's own headers go on first, so a connection that pins a different API version keeps it.
    ///     Where the credential goes is the provider's rule rather than the operator's choice: Anthropic rejects
    ///     a bearer token, so a profile saved under the plain API-key mode is sent the same way as one saved
    ///     under the x-api-key mode instead of failing with a 401 that says nothing about why.
    /// </remarks>
    /// <param name="request">The request about to be sent.</param>
    /// <param name="endpoint">The endpoint carrying the credential and the operator's headers.</param>
    internal static void ApplyRequiredHeaders(HttpRequestMessage request, ProviderEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(endpoint);

        foreach (var (name, value) in endpoint.DefaultHeaders ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (!string.IsNullOrWhiteSpace(endpoint.Secret))
        {
            request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, endpoint.Secret);
        }

        if (!request.Headers.Contains(VersionHeaderName))
        {
            request.Headers.TryAddWithoutValidation(VersionHeaderName, AnthropicVersion);
        }
    }

    private HttpRequestMessage CreateRequest(JsonObject payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildMessagesUri(this._endpoint))
        {
            Content = new StringContent(payload.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json"),
        };

        ApplyRequiredHeaders(request, this._endpoint);

        return request;
    }

    private static Uri BuildMessagesUri(ProviderEndpoint endpoint)
    {
        return ProviderEndpointAddress.For(endpoint, "messages");
    }

    private JsonObject BuildRequest(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var reasoning = options?.RawRepresentationFactory?.Invoke(this) as ProviderReasoningRequest;
        var thinkingBudget = ThinkingBudget(reasoning?.Effort ?? ProviderReasoningEffort.None);

        var payload = new JsonObject
        {
            ["model"] = this._model.RemoteModelId,

            // The cap covers the thinking budget as well as the answer, so an enabled budget is added to the
            // caller's cap rather than taken out of it — otherwise a model that thought hard would run out of
            // room mid-answer and return a truncated one.
            ["max_tokens"] = (options?.MaxOutputTokens ?? DefaultMaxTokens) + (thinkingBudget ?? 0),
        };

        // The system prompt is a top-level field here, not a message with a role, so system turns are lifted out
        // and joined rather than being sent in the conversation where Anthropic would reject them.
        var system = string.Join(
            "\n\n",
            messages.Where(message => message.Role == ChatRole.System)
                .Select(message => message.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        if (system.Length > 0)
        {
            payload["system"] = ToSystemField(system);
        }

        // Extended thinking fixes the sampling temperature, and a request that asks for both is rejected.
        if (options?.Temperature is { } temperature && thinkingBudget is null)
        {
            payload["temperature"] = temperature;
        }

        if (thinkingBudget is { } budget)
        {
            payload["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = budget,
            };
        }

        var conversation = new JsonArray();
        foreach (var message in messages.Where(message => message.Role != ChatRole.System))
        {
            if (ToAnthropicMessage(message) is { } translated)
            {
                conversation.Add(translated);
            }
        }

        // Anthropic requires at least one message and answers an empty array with a 400 naming the field. A
        // conversation can empty out here without the caller sending nothing: ToAnthropicMessage drops a turn
        // whose blocks are all empty or of a kind this driver does not translate, and system turns are carried
        // in "system" rather than here. Refused with the status the provider would have answered with, so the
        // shared classifier reads it as permanent and the cause names the host rather than the vendor.
        if (conversation.Count == 0)
        {
            throw new HttpRequestException(
                "The conversation holds nothing Anthropic can be sent: every turn was a system turn, empty, or "
                + "of a content kind this driver does not translate.",
                null,
                HttpStatusCode.BadRequest);
        }

        MarkConversationPrefixAsCacheable(conversation);
        payload["messages"] = conversation;

        if (options?.Tools is { Count: > 0 } tools)
        {
            payload["tools"] = ToAnthropicTools(tools);
        }

        return payload;
    }

    /// <summary>
    ///     Renders the system prompt, marking it as a cache breakpoint when it is big enough to be worth one.
    /// </summary>
    /// <remarks>
    ///     A breakpoint caches everything before it, and tool definitions precede the system prompt in Anthropic's
    ///     cache order, so this single mark covers both — the entire stable prefix of a review pass, which is
    ///     re-sent on every file and every tool-loop turn. A cache write costs a quarter more than a plain input
    ///     token and a read a tenth as much, so this pays for itself on the second call and loses only where a
    ///     prompt is used exactly once.
    /// </remarks>
    private static JsonNode ToSystemField(string system)
    {
        if (system.Length < PromptCachePolicy.MinimumCacheableChars)
        {
            return JsonValue.Create(system);
        }

        return new JsonArray
        {
            WithCacheControl(new JsonObject { ["type"] = "text", ["text"] = system }),
        };
    }

    /// <summary>
    ///     Marks the end of the conversation so the next turn can read it back instead of re-paying for it.
    /// </summary>
    /// <remarks>
    ///     An agentic tool loop re-sends the whole conversation each turn, growing it by one exchange. Moving the
    ///     breakpoint to the end each time means turn N+1 reads everything turn N sent and writes only the delta.
    ///     Skipped on a first turn, where there is no earlier call to have written a cache entry.
    /// </remarks>
    private static void MarkConversationPrefixAsCacheable(JsonArray conversation)
    {
        if (conversation.Count < 2 || EstimateChars(conversation) < PromptCachePolicy.MinimumCacheableChars)
        {
            return;
        }

        if (conversation[^1] is JsonObject last
            && last["content"] is JsonArray blocks
            && blocks.Count > 0
            && blocks[^1] is JsonObject block)
        {
            WithCacheControl(block);
        }
    }

    private static JsonObject WithCacheControl(JsonObject block)
    {
        block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        return block;
    }

    /// <summary>
    ///     Sizes the conversation so a cache marker is only spent where it can pay for itself. Characters rather
    ///     than tokens because this only has to clear a floor, and tokenising the conversation to answer it would
    ///     cost more than the marker saves.
    /// </summary>
    /// <remarks>
    ///     Marking cache points is specific to this protocol, not something the other providers omit. Anthropic
    ///     requires the caller to state where its cache may break; Gemini caches implicitly and offers nothing to
    ///     mark, and reports what it served from cache as <c>cachedContentTokenCount</c> instead. Both therefore
    ///     report a cached token count, which the review protocol reads.
    /// </remarks>
    /// <param name="conversation">The messages about to be sent.</param>
    /// <returns>The total length of the text carried by the conversation.</returns>
    private static long EstimateChars(JsonArray conversation)
    {
        long total = 0;

        foreach (var message in conversation.OfType<JsonObject>())
        {
            if (message["content"] is not JsonArray blocks)
            {
                continue;
            }

            foreach (var block in blocks.OfType<JsonObject>())
            {
                foreach (var field in new[] { "text", "content", "thinking" })
                {
                    if (block[field] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        total += text.Length;
                    }
                }
            }
        }

        return total;
    }

    /// <summary>
    ///     Turns a neutral effort level into Anthropic's own knob: a token budget the model may spend thinking.
    ///     Its floor is 1024, so the lowest rung is set clear of it.
    /// </summary>
    private static int? ThinkingBudget(ProviderReasoningEffort effort)
    {
        return effort switch
        {
            ProviderReasoningEffort.Low => 2048,
            ProviderReasoningEffort.Medium => 8192,
            ProviderReasoningEffort.High => 24576,
            _ => null,
        };
    }

    private static JsonObject? ToAnthropicMessage(ChatMessage message)
    {
        var blocks = new JsonArray();

        // A thinking block has to come back first and unaltered on the turn that produced it: with extended
        // thinking on, Anthropic verifies its own signature over the block and refuses an assistant turn whose
        // reasoning was dropped or edited before its tool call.
        foreach (var reasoning in message.Contents.OfType<TextReasoningContent>())
        {
            if (RestoreThinkingBlock(reasoning) is { } thinking)
            {
                blocks.Add(thinking);
            }
        }

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                    break;

                case FunctionCallContent call:
                    blocks.Add(
                        new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.CallId,
                            ["name"] = call.Name,
                            ["input"] = ToJsonNode(call.Arguments),
                        });
                    break;

                case FunctionResultContent result:
                    blocks.Add(
                        new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = result.CallId,
                            ["content"] = result.Result?.ToString() ?? string.Empty,
                        });
                    break;

                default:
                    continue;
            }
        }

        if (blocks.Count == 0)
        {
            return null;
        }

        // A tool result is carried by a USER turn here rather than by a role of its own, so a message that only
        // reports results is addressed as the user regardless of the role the caller gave it.
        // A restored thinking block belongs to an assistant turn, so it never makes a message on its own.
        var role = message.Contents.Any(content => content is FunctionResultContent) || message.Role == ChatRole.User
            ? "user"
            : "assistant";

        return new JsonObject { ["role"] = role, ["content"] = blocks };
    }

    /// <summary>
    ///     Recovers the provider's own thinking block from reasoning content that came back from it.
    /// </summary>
    /// <remarks>
    ///     The whole block is kept rather than its text, because what Anthropic verifies is the signature it
    ///     issued over the block as it sent it — and because a redacted block carries no readable text at all, only
    ///     an opaque payload that still has to be returned. Reasoning without a stored block did not come from
    ///     Anthropic (or arrived through a path that dropped it) and is left out: an unsigned thinking block is a
    ///     rejected request, which is worse than a turn without its reasoning.
    /// </remarks>
    private static JsonObject? RestoreThinkingBlock(TextReasoningContent reasoning)
    {
        if (string.IsNullOrEmpty(reasoning.ProtectedData))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(reasoning.ProtectedData) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonArray ToAnthropicTools(IEnumerable<AITool> tools)
    {
        var declared = new JsonArray();

        foreach (var tool in tools.OfType<AIFunction>())
        {
            declared.Add(
                new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = JsonNode.Parse(tool.JsonSchema.GetRawText()),
                });
        }

        return declared;
    }

    private static JsonNode? ToJsonNode(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return new JsonObject();
        }

        return JsonSerializer.SerializeToNode(arguments, SerializerOptions);
    }

    private static ChatResponse ParseResponse(string body, string modelId)
    {
        var payload = JsonNode.Parse(body) as JsonObject
                      ?? throw new HttpRequestException("Anthropic returned a response that was not an object.");

        var contents = new List<AIContent>();
        if (payload["content"] is JsonArray blocks)
        {
            foreach (var block in blocks.OfType<JsonObject>())
            {
                switch (block["type"]?.GetValue<string>())
                {
                    case "text" when block["text"]?.GetValue<string>() is { } text:
                        contents.Add(new TextContent(text));
                        break;

                    // Anthropic's extended thinking arrives as its own block. Surfacing it as reasoning content
                    // keeps it out of the answer text, where it would otherwise be indistinguishable from it. The
                    // block itself is kept alongside so the next turn can hand it back signed and intact.
                    case "thinking" when block["thinking"]?.GetValue<string>() is { } thinking:
                        contents.Add(new TextReasoningContent(thinking) { ProtectedData = block.ToJsonString() });
                        break;

                    // A redacted block is thinking the provider encrypted rather than showed. There is nothing to
                    // display, but it still has to be returned on the next turn or the turn is incomplete.
                    case "redacted_thinking":
                        contents.Add(new TextReasoningContent(string.Empty) { ProtectedData = block.ToJsonString() });
                        break;

                    case "tool_use":
                        contents.Add(
                            new FunctionCallContent(
                                block["id"]?.GetValue<string>() ?? string.Empty,
                                block["name"]?.GetValue<string>() ?? string.Empty,
                                ToArguments(block["input"])));
                        break;

                    default:
                        continue;
                }
            }
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = payload["model"]?.GetValue<string>() ?? modelId,
            ResponseId = payload["id"]?.GetValue<string>(),
            FinishReason = ToFinishReason(payload["stop_reason"]?.GetValue<string>()),
            Usage = ToUsage(payload["usage"] as JsonObject),
        };
    }

    private static Dictionary<string, object?>? ToArguments(JsonNode? input)
    {
        return input is JsonObject arguments
            ? arguments.ToDictionary(pair => pair.Key, pair => (object?)pair.Value?.DeepClone())
            : null;
    }

    private static ChatFinishReason? ToFinishReason(string? stopReason)
    {
        return stopReason switch
        {
            "end_turn" or "stop_sequence" => ChatFinishReason.Stop,
            "max_tokens" => ChatFinishReason.Length,
            "tool_use" => ChatFinishReason.ToolCalls,
            _ => null,
        };
    }

    /// <summary>
    ///     Carries Anthropic's usage counters as Anthropic reported them. Its input count excludes both cached
    ///     portions; adding them back is the driver's mapping. What leaves here is the vendor's own shape, so a
    ///     mapping that failed to normalize it is visible instead of already undone.
    /// </summary>
    private static UsageDetails? ToUsage(JsonObject? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = ReadCount(usage, "input_tokens"),
            OutputTokenCount = ReadCount(usage, "output_tokens"),
            CachedInputTokenCount = ReadCount(usage, "cache_read_input_tokens"),
            AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                [AnthropicUsageCounters.CacheCreation] = ReadCount(usage, "cache_creation_input_tokens"),
            },
        };
    }

    private static long ReadCount(JsonObject usage, string name)
    {
        return usage[name] is JsonValue value && value.TryGetValue<long>(out var count) ? count : 0;
    }

    /// <summary>What a non-success body says went wrong, or <see langword="null" /> where it says nothing.</summary>
    /// <remarks>
    ///     Every step is taken through the node's own type. This family accepts any host speaking the Messages
    ///     API, a gateway in front of one included, and a gateway answers with an error shape of its own: a body
    ///     of <c>{"error":"rate limited"}</c> holds a string where the vendor holds an object, and reaching
    ///     through it by name would raise <see cref="InvalidOperationException" /> while the caller is building
    ///     the exception that carries the status. The status would be lost with it, and a 429 would then be
    ///     classified as permanent. The message a gateway states directly under <c>error</c> is read as well as
    ///     the one the vendor states under <c>error.message</c>.
    /// </remarks>
    /// <param name="body">The body the endpoint answered with.</param>
    private static string? DescribeError(string body)
    {
        try
        {
            if ((JsonNode.Parse(body) as JsonObject)?["error"] is not { } error)
            {
                return null;
            }

            return AsText(error is JsonObject stated ? stated["message"] : error);
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
    }

    /// <summary>The text a node holds, or <see langword="null" /> where it holds anything else.</summary>
    /// <param name="node">The node to read.</param>
    private static string? AsText(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}

/// <summary>
///     The names Anthropic reports its own usage buckets under, shared by the client that parses them and the
///     driver that maps them onto the host's counters.
/// </summary>
public static class AnthropicUsageCounters
{
    /// <summary>Tokens written to Anthropic's prompt cache by this call.</summary>
    public const string CacheCreation = "cache_creation_input_tokens";
}
