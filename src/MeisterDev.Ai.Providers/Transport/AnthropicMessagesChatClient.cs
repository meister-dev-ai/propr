// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Speaks Anthropic's native Messages API as an <see cref="IChatClient" />.
/// </summary>
/// <remarks>
///     <para>
///         Written against the protocol rather than wrapped around a client library because Anthropic ships no
///         first-party .NET SDK, and the things the native class exists for — cache-control breakpoints, the
///         thinking block, its own usage counters — are exactly the details a third-party wrapper is slowest to
///         expose. Depending on one would put this library's roadmap behind someone else's.
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

    /// <summary>Anthropic requires an output cap, so one is sent when the caller expresses no preference.</summary>
    private const int DefaultMaxTokens = 8192;

    /// <summary>
    ///     Roughly the smallest prompt Anthropic will cache, in characters. Below its own minimum the provider
    ///     ignores a breakpoint outright, so marking a short prefix would spend one of the four a request is
    ///     allowed and buy nothing. Four characters per token is the usual approximation and does not need to be
    ///     exact — being wrong here costs a cache miss, not a failure.
    /// </summary>
    private const int MinimumCacheableChars = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ProviderEndpoint _endpoint;
    private readonly ProviderModelDescriptor _model;

    /// <summary>Initializes a new instance of the <see cref="AnthropicMessagesChatClient" /> class.</summary>
    /// <param name="httpClient">The egress-guarded client runtime traffic goes through.</param>
    /// <param name="endpoint">Where to reach the provider and how to authenticate.</param>
    /// <param name="model">The model this client is bound to.</param>
    public AnthropicMessagesChatClient(
        HttpClient httpClient,
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);

        this._httpClient = httpClient;
        this._endpoint = endpoint;
        this._model = model;
    }

    /// <inheritdoc />
    public AiProtocolMode NativeProtocol => AiProtocolMode.AnthropicMessages;

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var payload = this.BuildRequest([.. messages], options);
        using var request = this.CreateRequest(payload);
        using var response = await this._httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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
    public void Dispose()
    {
        // The HttpClient is owned by the factory that produced it, so it is deliberately not disposed here.
    }

    private HttpRequestMessage CreateRequest(JsonObject payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildMessagesUri(this._endpoint.BaseUrl))
        {
            Content = new StringContent(payload.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json"),
        };

        OpenAiCompatibleRequestFactory.ApplyCredential(request, this._endpoint);
        request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

        foreach (var header in this._endpoint.DefaultHeaders ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
    }

    private static Uri BuildMessagesUri(string baseUrl)
    {
        var builder = new UriBuilder(new Uri(baseUrl, UriKind.Absolute));
        builder.Path = $"{builder.Path.TrimEnd('/')}/messages";
        return builder.Uri;
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
        if (system.Length < MinimumCacheableChars)
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
        if (conversation.Count < 2 || EstimateChars(conversation) < MinimumCacheableChars)
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
    ///     Maps Anthropic's usage counters onto the normalized shape. Its input count EXCLUDES the cached
    ///     portions, unlike the OpenAI family where the total already contains them, so the buckets are added
    ///     back to keep "input tokens" meaning the same thing across providers.
    /// </summary>
    private static UsageDetails? ToUsage(JsonObject? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var input = ReadCount(usage, "input_tokens");
        var cacheRead = ReadCount(usage, "cache_read_input_tokens");
        var cacheWrite = ReadCount(usage, "cache_creation_input_tokens");

        return new UsageDetails
        {
            InputTokenCount = input + cacheRead + cacheWrite,
            OutputTokenCount = ReadCount(usage, "output_tokens"),
            CachedInputTokenCount = cacheRead,
            AdditionalCounts = new AdditionalPropertiesDictionary<long>
            {
                ["cache_creation_input_tokens"] = cacheWrite,
            },
        };
    }

    private static long ReadCount(JsonObject usage, string name)
    {
        return usage[name] is JsonValue value && value.TryGetValue<long>(out var count) ? count : 0;
    }

    private static string? DescribeError(string body)
    {
        try
        {
            return (JsonNode.Parse(body) as JsonObject)?["error"]?["message"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
    }
}
