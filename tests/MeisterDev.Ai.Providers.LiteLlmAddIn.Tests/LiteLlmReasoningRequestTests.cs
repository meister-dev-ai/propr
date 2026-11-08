// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

// The Responses API and the reasoning options of the OpenAI library are marked for evaluation only.
#pragma warning disable OPENAI001

namespace MeisterDev.Ai.Providers.LiteLlmAddIn.Tests;

/// <summary>
///     What a reasoning request in provider-neutral terms puts on the wire of each API a gateway forwards, read from
///     the recorded request bodies.
/// </summary>
/// <remarks>
///     OpenAI options that the host builds are a foreign type for the copy of the OpenAI library this add-in loads, so
///     the host hands a client of this family the neutral request. The Responses API receives every level by its name.
///     Chat Completions receives only the level none, because from GPT-5.4 on OpenAI refuses function tools there
///     together with any other effort, and a model behind the gateway that does not know the field can refuse the
///     request.
/// </remarks>
public sealed class LiteLlmReasoningRequestTests
{
    private const string Gateway = "https://gateway.example.com/v1";

    private const string ResponseBody =
        """
        {
          "id": "resp_fake", "object": "response", "created_at": 1770000000, "model": "m", "status": "completed",
          "output": [{
            "type": "message", "id": "msg_fake", "status": "completed", "role": "assistant",
            "content": [{ "type": "output_text", "text": "hi", "annotations": [] }]
          }],
          "parallel_tool_calls": false, "tool_choice": "auto", "tools": []
        }
        """;

    private const string ChatCompletionBody =
        """
        {
          "id": "chatcmpl-fake", "object": "chat.completion", "created": 1770000000, "model": "m",
          "choices": [{ "index": 0, "finish_reason": "stop", "message": { "role": "assistant", "content": "hi" } }],
          "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
        }
        """;

    private const string RemoteModelId = "gpt-6-luna";

    private static readonly ProviderModelDescriptor Model = new(
        Guid.NewGuid(),
        RemoteModelId,
        [ProviderDeclaredProtocolModes.Auto, LiteLlmProviderDriver.ResponsesProtocol, LiteLlmProviderDriver.ChatCompletionsProtocol]);

    // The host hands the neutral request only to a native-protocol client, and reads the protocol from it.
    [Theory]
    [InlineData(LiteLlmProviderDriver.ResponsesProtocol)]
    [InlineData(LiteLlmProviderDriver.ChatCompletionsProtocol)]
    public void TheClient_IsANativeProtocolClientOfTheBoundProtocol(string protocolMode)
    {
        using var chat = new LiteLlmProviderDriver().CreateChatClient(EndpointOn(new RecordingEndpoint(ResponseBody)), Model, protocolMode);

        Assert.Equal(protocolMode, Assert.IsAssignableFrom<INativeProtocolChatClient>(chat).NativeProtocol);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("xhigh")]
    public async Task ALevel_ReachesTheResponsesApiByItsName(string level)
    {
        var body = await SendAsync(LiteLlmProviderDriver.ResponsesProtocol, Reasoning(ProviderReasoningEffort.High, false, level));

        var reasoning = body.GetProperty("reasoning");
        Assert.Equal(level, reasoning.GetProperty("effort").GetString());
        Assert.False(reasoning.TryGetProperty("summary", out _));
    }

    [Fact]
    public async Task CapturedReasoning_AsksTheResponsesApiForTheSummary()
    {
        var body = await SendAsync(LiteLlmProviderDriver.ResponsesProtocol, Reasoning(ProviderReasoningEffort.High, true, "max"));

        var reasoning = body.GetProperty("reasoning");
        Assert.Equal("max", reasoning.GetProperty("effort").GetString());
        Assert.Equal("auto", reasoning.GetProperty("summary").GetString());
    }

    // A request that names no level is read from its neutral effort.
    [Fact]
    public async Task ARequestWithoutALevel_ReachesTheResponsesApiWithItsNeutralEffort()
    {
        var body = await SendAsync(LiteLlmProviderDriver.ResponsesProtocol, Reasoning(ProviderReasoningEffort.Medium, false));

        Assert.Equal("medium", body.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    // A request that names neither an effort nor a summary leaves no empty reasoning object, which a model that takes
    // no reasoning options refuses.
    [Fact]
    public async Task ARequestThatAsksForNothing_SendsTheResponsesApiNoReasoning()
    {
        var body = await SendAsync(LiteLlmProviderDriver.ResponsesProtocol, Reasoning(ProviderReasoningEffort.None, false));

        Assert.False(body.TryGetProperty("reasoning", out _));
    }

    // The host states the effort none in the shared reasoning setting for a model that accepts it. The reasoning options
    // that carry the summary hide that setting from the adapter, so its effort is carried over.
    [Fact]
    public async Task ASummaryWithoutALevel_KeepsTheEffortOfTheSharedSetting()
    {
        var options = Reasoning(ProviderReasoningEffort.None, true);
        options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None };

        var body = await SendAsync(LiteLlmProviderDriver.ResponsesProtocol, options);

        var reasoning = body.GetProperty("reasoning");
        Assert.Equal("none", reasoning.GetProperty("effort").GetString());
        Assert.Equal("auto", reasoning.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task TheLevelNone_ReachesChatCompletions()
    {
        var body = await SendAsync(LiteLlmProviderDriver.ChatCompletionsProtocol, Reasoning(ProviderReasoningEffort.None, false, "none"));

        Assert.Equal("none", body.GetProperty("reasoning_effort").GetString());
    }

    // From GPT-5.4 on, OpenAI refuses function tools on Chat Completions together with any effort other than none, and a
    // model behind the gateway that does not know the field can refuse the request.
    [Theory]
    [InlineData("high")]
    [InlineData("max")]
    public async Task AnyOtherLevel_LeavesChatCompletionsWithoutAnEffort(string level)
    {
        var body = await SendAsync(LiteLlmProviderDriver.ChatCompletionsProtocol, Reasoning(ProviderReasoningEffort.High, true, level));

        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    // A caller that also states an effort on the shared setting, for example because it took the protocol of another
    // model, still sends Chat Completions no effort other than none.
    [Fact]
    public async Task ASharedEffortOtherThanNone_DoesNotReachChatCompletions()
    {
        var options = Reasoning(ProviderReasoningEffort.High, false, "high");
        options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High };

        var body = await SendAsync(LiteLlmProviderDriver.ChatCompletionsProtocol, options);

        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    // Without a neutral request the options reach the adapter as the caller built them, including options of the
    // OpenAI library that a caller in the same load context builds itself.
    [Theory]
    [InlineData(LiteLlmProviderDriver.ResponsesProtocol, false)]
    [InlineData(LiteLlmProviderDriver.ResponsesProtocol, true)]
    [InlineData(LiteLlmProviderDriver.ChatCompletionsProtocol, false)]
    [InlineData(LiteLlmProviderDriver.ChatCompletionsProtocol, true)]
    public async Task OptionsWithoutANeutralRequest_ReachTheAdapterUnchanged(string protocolMode, bool callerBuildsLibraryOptions)
    {
        var usesResponsesApi = protocolMode == LiteLlmProviderDriver.ResponsesProtocol;
        var options = new ChatOptions
        {
            MaxOutputTokens = 512,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
            RawRepresentationFactory = callerBuildsLibraryOptions ? _ => LibraryOptions(usesResponsesApi) : null,
        };

        var sent = await SendAsync(protocolMode, options);
        var sentByTheAdapterAlone = await SendThroughTheAdapterAloneAsync(usesResponsesApi, options);

        Assert.Equal(sentByTheAdapterAlone.GetRawText(), sent.GetRawText());
    }

    // Options of the OpenAI library with the effort low, which a caller in this load context can build itself.
    private static object LibraryOptions(bool usesResponsesApi)
    {
        return usesResponsesApi
            ? new CreateResponseOptions
            {
                ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = ResponseReasoningEffortLevel.Low },
            }
            : new ChatCompletionOptions { ReasoningEffortLevel = ChatReasoningEffortLevel.Low };
    }

    /// <summary>Asks for reasoning the way the host does: neutrally, and only of a native-protocol client.</summary>
    private static ChatOptions Reasoning(ProviderReasoningEffort effort, bool captureReasoning, string? level = null)
    {
        return new ChatOptions
        {
            RawRepresentationFactory = client => client is INativeProtocolChatClient
                ? new ProviderReasoningRequest(effort, captureReasoning) { Level = level }
                : null,
        };
    }

    // Sends one call through a client of this family and returns the body of the request it sent.
    private static async Task<JsonElement> SendAsync(string protocolMode, ChatOptions options)
    {
        var wire = new RecordingEndpoint(protocolMode == LiteLlmProviderDriver.ResponsesProtocol ? ResponseBody : ChatCompletionBody);
        using var chat = new LiteLlmProviderDriver().CreateChatClient(EndpointOn(wire), Model, protocolMode);

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Review the change.")], options);

        return JsonDocument.Parse(Assert.Single(wire.Bodies)).RootElement;
    }

    // Sends the same call through the Microsoft.Extensions.AI adapter without this family's client around it.
    private static async Task<JsonElement> SendThroughTheAdapterAloneAsync(bool usesResponsesApi, ChatOptions options)
    {
        var wire = new RecordingEndpoint(usesResponsesApi ? ResponseBody : ChatCompletionBody);
        var credential = new ApiKeyCredential("virtual-key");
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(Gateway),
            Transport = new HttpClientPipelineTransport(new HttpClient(wire)),
        };
        using var chat = usesResponsesApi
            ? new OpenAIClient(credential, clientOptions).GetResponsesClient().AsIChatClient(RemoteModelId)
            : new ChatClient(RemoteModelId, credential, clientOptions).AsIChatClient();

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Review the change.")], options);

        return JsonDocument.Parse(Assert.Single(wire.Bodies)).RootElement;
    }

    private static ProviderEndpoint EndpointOn(HttpMessageHandler wire)
    {
        return new ProviderEndpoint("meisterdev/liteLlm", Gateway, LiteLlmProviderDriver.ApiKeyAuth, "virtual-key")
        {
            HostContext = new FakeProviderHostContext(wire),
        };
    }

    /// <summary>Records the body of each request and answers with the same body every time.</summary>
    private sealed class RecordingEndpoint(string responseBody) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
