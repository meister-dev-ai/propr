// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.GoogleVertexAddIn;

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

    /// <summary>
    ///     The property a refused prompt's reason is carried under on the response, worded as Google worded it.
    /// </summary>
    public const string PromptBlockReason = "google.promptFeedback.blockReason";

    /// <summary>
    ///     Where the part Google sent a function call in is kept between reading the call and sending it back.
    ///     The part is replayed rather than rebuilt, because what Google verifies on the next turn is the part
    ///     and not only the call inside it. Held on the call's additional properties because that is what
    ///     survives being serialized, which a review executed on a runner is.
    /// </summary>
    public const string CallPart = "google.functionCallPart";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IProviderHttpClientFactory? _http;
    private readonly IGoogleCredentialSource _credentials;
    private readonly ProviderEndpoint _endpoint;
    private readonly ProviderModelDescriptor _model;

    /// <summary>Initializes a new instance of the <see cref="GoogleGenerateContentChatClient" /> class.</summary>
    /// <param name="http">
    ///     The host's client factory, from which the egress-guarded client runtime traffic goes through is taken,
    ///     or null where the host supplied none.
    /// </param>
    /// <param name="credentials">Authenticates each request for the surface the endpoint is.</param>
    /// <param name="endpoint">Where to reach the provider.</param>
    /// <param name="model">The model this client is bound to.</param>
    /// <remarks>
    ///     The factory is held rather than a client, and the client is taken on the first call. A host builds one
    ///     of these to describe a family as well as to call it — the shared driver checks construct one against an
    ///     endpoint that belongs to no connection — and a client taken in the constructor would make describing
    ///     the family fail for want of a factory nothing was going to use.
    /// </remarks>
    public GoogleGenerateContentChatClient(
        IProviderHttpClientFactory? http,
        IGoogleCredentialSource credentials,
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(model);

        this._http = http;
        this._credentials = credentials;
        this._endpoint = endpoint;
        this._model = model;
    }

    /// <inheritdoc />
    public string NativeProtocol => GoogleVertexProviderDriver.GenerateContentProtocol;

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var payload = this.BuildRequest([.. messages], options);
        var uri = GoogleEndpointResolution.BuildModelUri(this._endpoint, this._model.RemoteModelId, GenerateContentMethod);

        // The endpoint's default query parameters are applied here, the same way the shared handler applies them
        // for a family that goes through it. This client builds its own request and reads the Vertex 'project'
        // value while composing the path, so without this a gateway parameter or a query-carried credential the
        // operator configured never reaches generateContent.
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            ProviderEndpointAddress.WithDefaultQuery(uri, this._endpoint.DefaultQueryParams))
        {
            Content = new StringContent(payload.ToJsonString(SerializerOptions), Encoding.UTF8, "application/json"),
        };
        await this._credentials.AuthenticateAsync(request, this._endpoint, cancellationToken).ConfigureAwait(false);

        foreach (var header in this._endpoint.DefaultHeaders ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Taken per call. The factory hands back a thin client over the host's pooled handler chain, so holding
        // one saves nothing, and a field assigned with ??= is written by every call that finds it empty.
        using var client = GoogleTransport.Client(this._http, ProviderHttpPurpose.Runtime);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
    /// <remarks>
    ///     Nothing to release. The client each call takes from the host's factory is disposed by that call, and
    ///     disposing one stops at the host's pooled handler chain, which the factory keeps and rotates on its own
    ///     schedule.
    /// </remarks>
    public void Dispose()
    {
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

        // Gemini matches a tool result to the call it answers by the function's name, so the name has to travel
        // with the result. A FunctionResultContent carries only the call id, and the call it answers is in an
        // earlier message, so the pairs are read off the whole conversation before any of it is translated.
        var calledFunctions = CalledFunctionsIn(messages);

        var contents = new JsonArray();
        foreach (var message in messages.Where(message => message.Role != ChatRole.System))
        {
            if (ToContent(message, calledFunctions) is { } translated)
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

    /// <summary>The function each call id names, read across the conversation.</summary>
    /// <param name="messages">The conversation as the caller composed it.</param>
    private static IReadOnlyDictionary<string, string> CalledFunctionsIn(IEnumerable<ChatMessage> messages)
    {
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var call in messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>())
        {
            if (!string.IsNullOrEmpty(call.CallId) && !string.IsNullOrEmpty(call.Name))
            {
                named[call.CallId] = call.Name;
            }
        }

        return named;
    }

    private static JsonObject? ToContent(ChatMessage message, IReadOnlyDictionary<string, string> calledFunctions)
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
                    parts.Add(RestoreCallPart(call) ?? BuildCallPart(call));
                    break;

                case FunctionResultContent result:
                    parts.Add(BuildResultPart(result, calledFunctions));
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
    // The function's name, with the call id beside it where the two differ. Writing the call id into 'name' put
    // an identifier where Gemini reads a function, so a result answered a call the model never made and the tool
    // turn went unmatched. The id still travels, in the field the protocol has for it, so a model that reads
    // both pairs them exactly.
    private static JsonObject BuildResultPart(
        FunctionResultContent result,
        IReadOnlyDictionary<string, string> calledFunctions)
    {
        var response = new JsonObject
        {
            ["name"] = calledFunctions.GetValueOrDefault(result.CallId ?? string.Empty) ?? result.CallId,
            ["response"] = new JsonObject { ["result"] = result.Result?.ToString() ?? string.Empty },
        };

        if (!string.IsNullOrEmpty(result.CallId)
            && !string.Equals(response["name"]?.GetValue<string>(), result.CallId, StringComparison.Ordinal))
        {
            response["id"] = result.CallId;
        }

        return new JsonObject { ["functionResponse"] = response };
    }

    private static JsonObject WithCallId(JsonObject call, FunctionCallContent content)
    {
        if (!string.Equals(content.CallId, content.Name, StringComparison.Ordinal))
        {
            call["id"] = content.CallId;
        }

        return call;
    }

    // The part Google sent, replayed as it arrived. Anything it carried beside the call — the signature it is
    // verified by today, and whatever is added to a part later — goes back with it.
    private static JsonObject? RestoreCallPart(FunctionCallContent call)
    {
        if (call.AdditionalProperties?.TryGetValue(CallPart, out var stored) != true
            || AsStoredPart(stored) is not { } part)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(part) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A call this family did not read from Google has no part to replay, so one is built from what the call
    // states. Nothing signs such a call, and Gemini asks for a signature only on one it issued itself.
    private static JsonObject BuildCallPart(FunctionCallContent call)
    {
        return new JsonObject
        {
            ["functionCall"] = WithCallId(
                new JsonObject
                {
                    ["name"] = call.Name,
                    ["args"] = ToJsonNode(call.Arguments),
                },
                call),
        };
    }

    // Read as text whatever the property holds: a value that came back through a serialized message is a JSON
    // value rather than the string it was stored as.
    private static string? AsStoredPart(object? stored)
    {
        var text = stored switch
        {
            string value => value,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonNode node => GoogleJson.AsText(node),
            _ => null,
        };

        return string.IsNullOrEmpty(text) ? null : text;
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

    // The parameters are translated, not forwarded: Google reads them as a protobuf message with a
    // closed field set and a single-valued type, which a JSON Schema document is not. A tool that comes to no
    // named parameters leaves the field off, which is how Google states a function that takes no arguments.
    private static JsonArray ToFunctionDeclarations(IEnumerable<AITool> tools)
    {
        var declared = new JsonArray();

        foreach (var tool in tools.OfType<AIFunction>())
        {
            var declaration = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
            };

            if (GoogleSchema.ToFunctionParameters(tool.JsonSchema) is { } parameters)
            {
                declaration["parameters"] = parameters;
            }

            declared.Add(declaration);
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

        foreach (var part in (GoogleJson.Field(candidate?["content"], "parts") as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (part["functionCall"] is JsonObject call)
            {
                calledATool = true;
                var name = GoogleJson.AsText(call["name"]) ?? string.Empty;
                var invoked = new FunctionCallContent(
                    GoogleJson.AsText(call["id"]) ?? name,
                    name,
                    ToArguments(call["args"]));

                // Gemini signs the call as well as the thinking that led to it, and refuses a later turn whose
                // calls come back without that signature. The signature sits on the part beside the call rather
                // than inside it, so the part is kept whole and nothing about its shape has to be known here.
                invoked.AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [CallPart] = part.ToJsonString(),
                };

                contents.Add(invoked);
                continue;
            }

            if (GoogleJson.AsText(part["text"]) is not { } text)
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

        // Google answers a prompt it refuses with 200, no candidates, and the reason under promptFeedback. Left
        // unread that is an empty assistant turn with no finish reason, which a caller cannot tell apart from a
        // model that had nothing to say. A reason of any wording means the prompt was refused; the wording is
        // not matched against a list, because Google adds reasons.
        var blockReason = candidate is null
            ? GoogleJson.AsText(GoogleJson.Field(payload["promptFeedback"], "blockReason"))
            : null;

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = GoogleJson.AsText(payload["modelVersion"]) ?? modelId,
            ResponseId = GoogleJson.AsText(payload["responseId"]),
            FinishReason = blockReason is null
                ? ToFinishReason(GoogleJson.AsText(candidate?["finishReason"]), calledATool)
                : ChatFinishReason.ContentFilter,
            Usage = ToUsage(payload["usageMetadata"] as JsonObject),
        };

        if (blockReason is not null)
        {
            // Google's own wording, carried beside the finish reason. The finish reason states the category; the
            // wording states which rule refused the prompt, which an operator acts on.
            response.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [PromptBlockReason] = blockReason,
            };
        }

        return response;
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
    ///     Carries Google's usage counters as Google reported them. Its prompt count already contains the cached
    ///     portion, and its thinking tokens sit outside the candidate count while still being billed as output;
    ///     adding those in is the driver's mapping, so what leaves here is the vendor's own shape.
    /// </summary>
    private static UsageDetails? ToUsage(JsonObject? usage)
    {
        if (usage is null)
        {
            return null;
        }

        return new UsageDetails
        {
            InputTokenCount = ReadCount(usage, "promptTokenCount"),
            OutputTokenCount = ReadCount(usage, "candidatesTokenCount"),
            CachedInputTokenCount = ReadCount(usage, "cachedContentTokenCount"),
            ReasoningTokenCount = ReadCount(usage, "thoughtsTokenCount"),
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
            // Read the way the rest of this file reads a node, because GetValue raises on a node of another
            // kind. A provider answering with a numeric or structured error message would otherwise raise from
            // inside the handler describing its failure, and the HTTP status that caused it would be lost.
            var message = (JsonNode.Parse(body) as JsonObject)?["error"]?["message"];
            if (message is JsonValue value && value.TryGetValue<string>(out var described))
            {
                return described;
            }
        }
        catch (JsonException)
        {
            // Falls through to the body below.
        }

        return string.IsNullOrWhiteSpace(body) ? null : body;
    }
}
