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
///     Speaks Google's generateContent protocol as an <see cref="IChatClient" />, on either the Gemini API or
///     Vertex AI.
/// </summary>
/// <remarks>
///     <para>
///         Written against the protocol because Google publishes no first-party Microsoft.Extensions.AI adapter,
///         and the alternatives are community packages whose release cadence would decide when this system can
///         use a new Gemini capability.
///     </para>
///     <para>
///         Four shape differences from the OpenAI family drive the translation: the assistant is called
///         <c>model</c>, the system prompt is a separate <c>systemInstruction</c>, a tool result is a
///         <em>user</em> turn carrying a <c>functionResponse</c> part, and thinking arrives as ordinary parts
///         flagged <c>thought</c> — which have to be handed back with the signature Google issued for them.
///     </para>
/// </remarks>
public sealed class GoogleGenerateContentChatClient : INativeProtocolChatClient
{
    /// <summary>The method this client calls on a model.</summary>
    public const string GenerateContentMethod = "generateContent";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IGoogleCredentialSource _credentials;
    private readonly ProviderEndpoint _endpoint;
    private readonly ProviderModelDescriptor _model;

    /// <summary>Initializes a new instance of the <see cref="GoogleGenerateContentChatClient" /> class.</summary>
    /// <param name="httpClient">The egress-guarded client runtime traffic goes through.</param>
    /// <param name="credentials">Authenticates each request for the surface the endpoint is.</param>
    /// <param name="endpoint">Where to reach the provider.</param>
    /// <param name="model">The model this client is bound to.</param>
    public GoogleGenerateContentChatClient(
        HttpClient httpClient,
        IGoogleCredentialSource credentials,
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);

        this._httpClient = httpClient;
        this._credentials = credentials;
        this._endpoint = endpoint;
        this._model = model;
    }

    /// <inheritdoc />
    public AiProtocolMode NativeProtocol => AiProtocolMode.GoogleGenerateContent;

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var payload = this.BuildRequest([.. messages], options);
        var uri = GoogleEndpointResolution.BuildModelUri(this._endpoint, this._model.RemoteModelId, GenerateContentMethod);

        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json"),
        };
        await this._credentials.AuthenticateAsync(request, this._endpoint, cancellationToken).ConfigureAwait(false);

        foreach (var header in this._endpoint.DefaultHeaders ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await this._httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Thrown with the status attached so the shared classifier can decide whether it is worth retrying.
            throw new HttpRequestException(
                $"Google rejected the request: {DescribeError(body) ?? response.ReasonPhrase}",
                null,
                response.StatusCode);
        }

        return ParseResponse(body, this._model.RemoteModelId);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Streaming is not implemented: the review loop does not stream, and a half-built reader that dropped
    ///     tool calls would be worse than an honest refusal.
    /// </remarks>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("The Google driver does not implement streaming responses. Use GetResponseAsync.");
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
        // The HttpClient is owned by the factory that produced it.
    }

    private JsonObject BuildRequest(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var payload = new JsonObject();

        var system = string.Join(
            "\n\n",
            messages.Where(message => message.Role == ChatRole.System)
                .Select(message => message.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        if (system.Length > 0)
        {
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = system } },
            };
        }

        var contents = new JsonArray();
        foreach (var message in messages.Where(message => message.Role != ChatRole.System))
        {
            if (ToContent(message) is { } translated)
            {
                contents.Add(translated);
            }
        }

        payload["contents"] = contents;

        var generation = new JsonObject();
        if (options?.Temperature is { } temperature)
        {
            generation["temperature"] = temperature;
        }

        if (options?.MaxOutputTokens is { } maxOutputTokens)
        {
            generation["maxOutputTokens"] = maxOutputTokens;
        }

        // Google expresses reasoning as a thinking budget and, separately, as whether the thoughts come back
        // at all. A budget of -1 lets the model decide how much to think, which is the closest thing it has to
        // "as much as is useful".
        var reasoning = options?.RawRepresentationFactory?.Invoke(this) as ProviderReasoningRequest;
        if (ThinkingBudget(reasoning?.Effort ?? ProviderReasoningEffort.None) is { } budget)
        {
            generation["thinkingConfig"] = new JsonObject
            {
                ["thinkingBudget"] = budget,
                ["includeThoughts"] = reasoning?.CaptureReasoning ?? false,
            };
        }

        if (generation.Count > 0)
        {
            payload["generationConfig"] = generation;
        }

        if (options?.Tools is { Count: > 0 } tools && ToFunctionDeclarations(tools) is { Count: > 0 } declared)
        {
            payload["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = declared } };
        }

        return payload;
    }

    private static int? ThinkingBudget(ProviderReasoningEffort effort)
    {
        return effort switch
        {
            ProviderReasoningEffort.Low => 2048,
            ProviderReasoningEffort.Medium => 8192,
            ProviderReasoningEffort.High => -1,
            _ => null,
        };
    }

    private static JsonObject? ToContent(ChatMessage message)
    {
        var parts = new JsonArray();

        // A thought part has to go back with the signature Google issued over it, ahead of the call it led to,
        // or a model that reasoned before calling a tool loses the reasoning it is about to be asked to continue.
        foreach (var reasoning in message.Contents.OfType<TextReasoningContent>())
        {
            if (RestoreThoughtPart(reasoning) is { } thought)
            {
                parts.Add(thought);
            }
        }

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    parts.Add(new JsonObject { ["text"] = text.Text });
                    break;

                case FunctionCallContent call:
                    parts.Add(
                        new JsonObject
                        {
                            ["functionCall"] = WithCallId(
                                new JsonObject
                                {
                                    ["name"] = call.Name,
                                    ["args"] = ToJsonNode(call.Arguments),
                                },
                                call),
                        });
                    break;

                case FunctionResultContent result:
                    parts.Add(
                        new JsonObject
                        {
                            ["functionResponse"] = new JsonObject
                            {
                                ["name"] = result.CallId,
                                ["response"] = new JsonObject { ["result"] = result.Result?.ToString() ?? string.Empty },
                            },
                        });
                    break;

                default:
                    continue;
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        // The assistant is called "model" here, and a tool result is a user turn rather than a role of its own.
        var role = message.Contents.Any(content => content is FunctionResultContent) || message.Role == ChatRole.User
            ? "user"
            : "model";

        return new JsonObject { ["role"] = role, ["parts"] = parts };
    }

    // Google identifies a function call by its name unless the model issued an id for it, which it does when
    // several calls are in flight at once. Echoing the id back is what keeps those results paired.
    private static JsonObject WithCallId(JsonObject call, FunctionCallContent content)
    {
        if (!string.Equals(content.CallId, content.Name, StringComparison.Ordinal))
        {
            call["id"] = content.CallId;
        }

        return call;
    }

    private static JsonObject? RestoreThoughtPart(TextReasoningContent reasoning)
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

    private static JsonArray ToFunctionDeclarations(IEnumerable<AITool> tools)
    {
        var declared = new JsonArray();

        foreach (var tool in tools.OfType<AIFunction>())
        {
            declared.Add(
                new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.JsonSchema.GetRawText()),
                });
        }

        return declared;
    }

    private static JsonNode? ToJsonNode(IDictionary<string, object?>? arguments)
    {
        return arguments is null || arguments.Count == 0
            ? new JsonObject()
            : JsonSerializer.SerializeToNode(arguments, SerializerOptions);
    }

    private static ChatResponse ParseResponse(string body, string modelId)
    {
        var payload = JsonNode.Parse(body) as JsonObject
                      ?? throw new HttpRequestException("Google returned a response that was not an object.");

        var candidate = (payload["candidates"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
        var contents = new List<AIContent>();
        var calledATool = false;

        foreach (var part in (candidate?["content"]?["parts"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (part["functionCall"] is JsonObject call)
            {
                calledATool = true;
                var name = call["name"]?.GetValue<string>() ?? string.Empty;
                contents.Add(
                    new FunctionCallContent(
                        call["id"]?.GetValue<string>() ?? name,
                        name,
                        ToArguments(call["args"])));
                continue;
            }

            if (part["text"]?.GetValue<string>() is not { } text)
            {
                continue;
            }

            // A thought is an ordinary text part wearing a flag. Left unread it would be indistinguishable from
            // the answer; the whole part is kept so its signature can go back with it.
            if (IsThought(part))
            {
                contents.Add(new TextReasoningContent(text) { ProtectedData = part.ToJsonString() });
            }
            else
            {
                contents.Add(new TextContent(text));
            }
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = payload["modelVersion"]?.GetValue<string>() ?? modelId,
            ResponseId = payload["responseId"]?.GetValue<string>(),
            FinishReason = ToFinishReason(candidate?["finishReason"]?.GetValue<string>(), calledATool),
            Usage = ToUsage(payload["usageMetadata"] as JsonObject),
        };
    }

    private static bool IsThought(JsonObject part)
    {
        return part["thought"] is JsonValue flag && flag.TryGetValue<bool>(out var thought) && thought;
    }

    private static Dictionary<string, object?>? ToArguments(JsonNode? input)
    {
        return input is JsonObject arguments
            ? arguments.ToDictionary(pair => pair.Key, pair => (object?)pair.Value?.DeepClone())
            : null;
    }

    private static ChatFinishReason? ToFinishReason(string? finishReason, bool calledATool)
    {
        if (calledATool)
        {
            return ChatFinishReason.ToolCalls;
        }

        return finishReason switch
        {
            "STOP" => ChatFinishReason.Stop,
            "MAX_TOKENS" => ChatFinishReason.Length,
            "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" => ChatFinishReason.ContentFilter,
            _ => null,
        };
    }

    /// <summary>
    ///     Maps Google's usage counters. Its prompt count already contains the cached portion, so unlike
    ///     Anthropic nothing is added back — but its thinking tokens sit outside the candidate count while still
    ///     being billed as output, so those are added in.
    /// </summary>
    private static UsageDetails? ToUsage(JsonObject? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var thoughts = ReadCount(usage, "thoughtsTokenCount");

        return new UsageDetails
        {
            InputTokenCount = ReadCount(usage, "promptTokenCount"),
            OutputTokenCount = ReadCount(usage, "candidatesTokenCount") + thoughts,
            CachedInputTokenCount = ReadCount(usage, "cachedContentTokenCount"),
            ReasoningTokenCount = thoughts,
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
